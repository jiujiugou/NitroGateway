using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NitroGateway.Collection;
using NitroGateway.Domain.Devices;
using NitroGateway.Domain.Protocols;

// ═══════════════════════════════════════════════════════
//  NitroGateway 闭环验证 — 纯内存，无需外部设备
// ═══════════════════════════════════════════════════════

int passed = 0;
int failed = 0;

void Assert(string name, bool condition, string detail = "")
{
    if (condition) { Console.WriteLine($"  ✅ {name}"); passed++; }
    else { Console.WriteLine($"  ❌ {name}  {detail}"); failed++; }
}

void Section(string title) =>
    Console.WriteLine($"\n┌─ {title}\n{new string('─', 60)}");


// ═══════════════════════════════════════════════════════
//  1. Pipeline — 类型转换 + 缩放
// ═══════════════════════════════════════════════════════
Section("1. Pipeline — Float");

var pipeline = new PointValuePipeline();
var deviceId = Guid.NewGuid();

var floatPt = new DevicePoint { Id = Guid.NewGuid(), Name = "Temp", Address = "40001", DataType = DataType.Float, ScaleFactor = 1.0 };
var floatRaw = new RawPointValue { Point = floatPt, Value = 12.5d, Timestamp = DateTime.UtcNow };
var floatSnap = pipeline.Process(deviceId, [floatRaw])[0];
Assert("Float 12.5 → {0:N1}", Math.Abs((double)floatSnap.Value! - 12.5) < 0.01, $"={floatSnap.Value}");
Assert("Quality = Good", floatSnap.Quality == QualityCode.Good);
Assert("DeviceId 传入", floatSnap.DeviceId == deviceId);

Section("1b. Pipeline — Int16 / Bool");

var i16pt = new DevicePoint { Id = Guid.NewGuid(), Name = "Level", Address = "40002", DataType = DataType.Int16 };
var i16raw = new RawPointValue { Point = i16pt, Value = 42, Timestamp = DateTime.UtcNow };
var i16snap = pipeline.Process(deviceId, [i16raw])[0];
Assert("Int16 42", Convert.ToInt16(i16snap.Value) == 42);

var boolPt = new DevicePoint { Id = Guid.NewGuid(), Name = "Running", Address = "00001", DataType = DataType.Bool };
var boolRaw = new RawPointValue { Point = boolPt, Value = true, Timestamp = DateTime.UtcNow };
var boolSnap = pipeline.Process(deviceId, [boolRaw])[0];
Assert("Bool true", (bool)boolSnap.Value!);

Section("1c. Pipeline — 缩放");

var scaledPt = new DevicePoint { Id = Guid.NewGuid(), Name = "Pressure", Address = "40003", DataType = DataType.Int16, ScaleFactor = 0.1, ScaleOffset = 5 };
var scaledRaw = new RawPointValue { Point = scaledPt, Value = 100, Timestamp = DateTime.UtcNow };
var scaledSnap = pipeline.Process(deviceId, [scaledRaw])[0];
Assert("100×0.1+5 = 15.0", Math.Abs((double)scaledSnap.Value! - 15.0) < 0.01, $"={scaledSnap.Value}");

//  2. Pipeline — 死区
Section("2. Pipeline — 死区");

var dbPt = new DevicePoint { Id = Guid.NewGuid(), Name = "SlowT", Address = "40004", DataType = DataType.Float, Deadband = 0.5 };

var r1 = pipeline.Process(deviceId, [new RawPointValue { Point = dbPt, Value = 60.0d, Timestamp = DateTime.UtcNow }]);
Assert("60.0 输出", r1.Count == 1);

var r2 = pipeline.Process(deviceId, [new RawPointValue { Point = dbPt, Value = 60.25d, Timestamp = DateTime.UtcNow }]);
Assert("60.25 变化 0.25 < 死区 0.5 → 丢弃", r2.Count == 0, $"实际 {r2.Count} 个");

var r3 = pipeline.Process(deviceId, [new RawPointValue { Point = dbPt, Value = 75.0d, Timestamp = DateTime.UtcNow }]);
Assert("75.0 变化 15 > 死区 → 输出", r3.Count == 1);

//  3. Pipeline — null 值不崩
Section("3. Pipeline — null 值 → 跳过");

var badRaw = new RawPointValue { Point = floatPt, Value = null!, Timestamp = DateTime.UtcNow };
var badResult = pipeline.Process(deviceId, [badRaw]);
Assert("null 值不抛异常", badResult.Count >= 0);

// ═══════════════════════════════════════════════════════
//  4. DeviceHealthMonitor
// ═══════════════════════════════════════════════════════
Section("4. HealthMonitor — Offline 阈值");

var did = Guid.NewGuid();
var offline = false;
var mlogger = NullLogger<NitroGateway.DeviceManagement.DeviceHealthMonitor>.Instance;
var monitor = new NitroGateway.DeviceManagement.DeviceHealthMonitor(mlogger);
monitor.ThresholdReached += (_, s) => { if (s == DeviceStatus.Offline) offline = true; };

for (int i = 0; i < 9; i++) monitor.ReportFailure(did, "timeout");
Assert("9 次失败不触发", !offline);
monitor.ReportFailure(did, "timeout");
Assert("第 10 次触发 Offline", offline);

Section("4b. HealthMonitor — Online 恢复");

var online = false;
var m2 = new NitroGateway.DeviceManagement.DeviceHealthMonitor(mlogger);
m2.ThresholdReached += (_, s) => { if (s == DeviceStatus.Online) online = true; };

// 先标记失败再恢复
for (int i = 0; i < 10; i++) m2.ReportFailure(did, "timeout");
for (int i = 0; i < 2; i++) m2.ReportSuccess(did);
Assert("2 次成功不触发 Online", !online);
m2.ReportSuccess(did);
Assert("第 3 次成功触发 Online", online);

// ═══════════════════════════════════════════════════════
//  5. Scheduler
// ═══════════════════════════════════════════════════════
Section("5. Scheduler — 定时触发");

int count = 0;
var sch = new NitroGateway.Scheduler.SchedulerEngine(NullLogger<NitroGateway.Scheduler.SchedulerEngine>.Instance);
sch.Register("t", 200, _ => { count++; return Task.CompletedTask; });
var cts = new CancellationTokenSource();
var _ = sch.RunAsync(cts.Token);
await Task.Delay(650);
cts.Cancel();
Assert("200ms 间隔 650ms → ≥3 次", count >= 3, $"实际 {count}");

// ═══════════════════════════════════════════════════════
//  6. DomainMapper
// ═══════════════════════════════════════════════════════
Section("6. DomainMapper — Device 双向映射");

var dev = new Device
{
    Id = Guid.NewGuid(), Name = "PLC-1", Protocol = ProtocolIdentifier.Modbus,
    Connection = new DeviceConnection { Endpoint = "192.168.1.100:502", Parameters = new() { ["UnitId"] = 1, ["Endian"] = "ABCD" } },
    Status = DeviceStatus.Online
};

var ent = NitroGateway.Infrastructure.Sqlite.DomainMapper.ToEntity(dev);
Assert("→ Entity Name", ent.Name == "PLC-1");
Assert("→ Entity Protocol", ent.ProtocolName == "Modbus");
Assert("→ Entity Status 字符串", ent.Status == "Online");
Assert("→ Entity ConnectionParams JSON", ent.ConnectionParams!.Contains("UnitId"));

var back = NitroGateway.Infrastructure.Sqlite.DomainMapper.ToDomain(ent);
Assert("← Device Name 还原", back.Name == dev.Name);
Assert("← Device Protocol 还原", back.Protocol.Name == "Modbus");
Assert("← Device Endpoint 还原", back.Connection.Endpoint == "192.168.1.100:502");

// ═══════════════════════════════════════════════════════
Section($"结果: {passed} 通过 / {failed} 失败 (共 {passed + failed})");

if (failed > 0) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\n❌ 有验证失败"); Console.ResetColor(); Environment.Exit(1); }
else { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine("\n✅ 全部通过 — 闭环 OK，可接外部设备验证"); Console.ResetColor(); }
