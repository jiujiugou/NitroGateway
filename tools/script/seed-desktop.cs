#:package Microsoft.Data.Sqlite@10.0.12
// seed-desktop.cs — Seed Modbus TCP devices + points directly into the running
// WPF desktop's SQLite DB (the desktop has no REST API). The desktop's
// DeviceSnapshotCache has a 10s TTL, so inserted devices are picked up
// automatically within ~10s (no restart needed).
//
// Run (single-file app, .NET 10+):
//   dotnet run tools/script/seed-desktop.cs -- --csv-dir tools/script --count 50
// Or via wrapper:
//   pwsh tools/script/seed-desktop.ps1 -Count 50
//
// Args:
//   --db <path>          desktop nitrogateway.db (default %LOCALAPPDATA%\NitroGateway\nitrogateway.db)
//   --csv-dir <dir>      dir containing points-device-NN.csv (NN = UnitId)
//   --endpoint <ip:port> Modbus TCP endpoint (default 127.0.0.1:15020)
//   --start-unit <n>     first UnitId (default 1)
//   --count <n>          number of devices (default 50)
//   --name-prefix <s>    device name prefix (default LoadDev -> LoadDev-001)
//   --replace            delete+recreate same-named devices (re-import points)
using Microsoft.Data.Sqlite;
using System.Globalization;

string db = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\NitroGateway\nitrogateway.db");
string csvDir = Directory.GetCurrentDirectory();
string endpoint = "127.0.0.1:15020";
int startUnit = 1, count = 50;
string prefix = "LoadDev";
bool replace = false;
bool remove = false;
string? siteIdArg = null;

for (int i = 0; i < args.Length; i++)
{
    string Need() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
    switch (args[i])
    {
        case "--db": db = Need(); break;
        case "--csv-dir": csvDir = Need(); break;
        case "--endpoint": endpoint = Need(); break;
        case "--start-unit": startUnit = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--count": count = int.Parse(Need(), CultureInfo.InvariantCulture); break;
        case "--name-prefix": prefix = Need(); break;
        case "--site-id": siteIdArg = Need(); break;
        case "--replace": replace = true; break;
        case "--remove": remove = true; break;
        case "-h" or "--help":
            Console.WriteLine("see header of this file for usage");
            return 0;
        default: throw new ArgumentException($"unknown arg: {args[i]}");
    }
}

if (!File.Exists(db)) { Console.Error.WriteLine($"DB not found: {db}"); return 2; }
if (!Directory.Exists(csvDir)) { Console.Error.WriteLine($"CSV dir not found: {csvDir}"); return 2; }

var csb = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 60 };
using var conn = new SqliteConnection(csb.ConnectionString);
conn.Open();
using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA busy_timeout=60000;"; pragma.ExecuteNonQuery(); }

// --remove: delete all devices whose name matches "<prefix>-%" and their points.
if (remove)
{
    using var rtx = conn.BeginTransaction();
    int dpCount, ddCount;
    using (var dp = conn.CreateCommand())
    {
        dp.Transaction = rtx;
        dp.CommandText = "DELETE FROM points WHERE DeviceId IN (SELECT Id FROM devices WHERE Name LIKE $p);";
        dp.Parameters.AddWithValue("$p", prefix + "-%");
        dpCount = dp.ExecuteNonQuery();
    }
    using (var dd = conn.CreateCommand())
    {
        dd.Transaction = rtx;
        dd.CommandText = "DELETE FROM devices WHERE Name LIKE $p;";
        dd.Parameters.AddWithValue("$p", prefix + "-%");
        ddCount = dd.ExecuteNonQuery();
    }
    rtx.Commit();
    Console.WriteLine($"removed: devices={ddCount} points={dpCount}; db={db} prefix={prefix}-");
    return 0;
}

// Resolve site id: explicit --site-id > desktop site.json > existing device > "default".
static string? ReadDesktopSiteId()
{
    try
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroGateway", "site.json");
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("siteId", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var v = e.GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }
    catch { /* site.json 缺失/损坏：回退 */ }
    return null;
}

string siteId = siteIdArg ?? ReadDesktopSiteId() ?? "";
if (siteId.Length == 0)
{
    using var siteCmd = conn.CreateCommand();
    siteCmd.CommandText = "SELECT SiteId FROM devices WHERE SiteId IS NOT NULL AND SiteId<>'' LIMIT 1;";
    if (siteCmd.ExecuteScalar() is string s && s.Length > 0) siteId = s;
}
if (siteId.Length == 0) siteId = "default";

var existing = new Dictionary<string, string>(StringComparer.Ordinal);
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT Id, Name FROM devices;";
    using var r = cmd.ExecuteReader();
    while (r.Read()) existing[r.GetString(1)] = r.GetString(0);
}

string now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
int ok = 0, skip = 0, fail = 0;

for (int k = 0; k < count; k++)
{
    int unit = startUnit + k;
    string name = $"{prefix}-{unit:D3}";
    string csvPath = Path.Combine(csvDir, $"points-device-{unit:D2}.csv");
    if (!File.Exists(csvPath)) csvPath = Path.Combine(csvDir, $"points-device-{unit:D3}.csv");
    if (!File.Exists(csvPath)) { Console.WriteLine($"[skip] unit={unit} missing CSV: {csvPath}"); skip++; continue; }

    try
    {
        using var tx = conn.BeginTransaction();

        bool exists = existing.TryGetValue(name, out var oldId);
        if (exists && !replace) { tx.Rollback(); Console.WriteLine($"[reuse] {name}"); skip++; continue; }
        if (exists)
        {
            using (var dp = conn.CreateCommand())
            {
                dp.Transaction = tx;
                dp.CommandText = "DELETE FROM points WHERE DeviceId=$id;";
                dp.Parameters.AddWithValue("$id", oldId!);
                dp.ExecuteNonQuery();
            }
            using var dd = conn.CreateCommand();
            dd.Transaction = tx;
            dd.CommandText = "DELETE FROM devices WHERE Id=$id;";
            dd.Parameters.AddWithValue("$id", oldId!);
            dd.ExecuteNonQuery();
        }

        // EF Core SQLite 以大写文本存 Guid：必须大写，否则仓储 GetByIdAsync/按 DeviceId 查点位匹配不到
        string deviceId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        using (var insDev = conn.CreateCommand())
        {
            insDev.Transaction = tx;
            insDev.CommandText = @"INSERT INTO devices
(Id,Name,Description,ProtocolName,ProtocolDialect,Endpoint,ConnectTimeoutMs,RequestTimeoutMs,RetryCount,Status,ConnectionParams,UpdatedAt,IsDeleted,SiteId)
VALUES ($id,$name,NULL,'Modbus','TCP',$ep,3000,5000,3,'Unknown',$params,$upd,0,$site);";
            insDev.Parameters.AddWithValue("$id", deviceId);
            insDev.Parameters.AddWithValue("$name", name);
            insDev.Parameters.AddWithValue("$ep", endpoint);
            insDev.Parameters.AddWithValue("$params", $"{{\"UnitId\":{unit}}}");
            insDev.Parameters.AddWithValue("$upd", now);
            insDev.Parameters.AddWithValue("$site", siteId);
            insDev.ExecuteNonQuery();
        }

        int points = 0;
        foreach (var raw in File.ReadLines(csvPath).Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cols = line.Split(',');
            if (cols.Length < 3) continue;
            bool enabled = cols.Length < 4 || !bool.TryParse(cols[3].Trim(), out var en) || en;
            if (!enabled) continue;

            using var insPt = conn.CreateCommand();
            insPt.Transaction = tx;
            insPt.CommandText = @"INSERT INTO points
(Id,DeviceId,Name,Address,Description,DataType,Access,Enabled,ScanIntervalMs,Deadband,ScaleFactor,ScaleOffset,MinLimit,MaxLimit,UpdatedAt,IsDeleted)
VALUES ($id,$dev,$name,$addr,NULL,$type,'ReadOnly',1,0,0,1,0,NULL,NULL,$upd,0);";
            insPt.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D").ToUpperInvariant());
            insPt.Parameters.AddWithValue("$dev", deviceId);
            insPt.Parameters.AddWithValue("$name", cols[0].Trim());
            insPt.Parameters.AddWithValue("$addr", cols[1].Trim());
            insPt.Parameters.AddWithValue("$type", cols[2].Trim());
            insPt.Parameters.AddWithValue("$upd", now);
            insPt.ExecuteNonQuery();
            points++;
        }

        tx.Commit();
        Console.WriteLine($"[ok] {name} unit={unit} device={deviceId} points={points}");
        ok++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[fail] {name} unit={unit}: {ex.Message}");
        fail++;
    }
}

Console.WriteLine();
Console.WriteLine($"done: ok={ok} skip={skip} fail={fail}; db={db} endpoint={endpoint} siteId={siteId}");
return fail > 0 ? 1 : 0;
