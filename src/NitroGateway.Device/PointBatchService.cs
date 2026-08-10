using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Extensions.Logging;
using NitroGateway.Domain.Devices;
using NitroGateway.Shared;

namespace NitroGateway.DeviceManagement;

/// <summary>
/// 点位批量服务。CSV 导入/导出、地址自动递增、名称模板替换。
/// 注册为 Singleton（无状态）。
/// </summary>
public sealed class PointBatchService
{
    private readonly ILogger<PointBatchService> _logger;

    public PointBatchService(ILogger<PointBatchService> logger)
    {
        _logger = logger;
    }

    // ════════════════════════════════════════════
    //  CSV 导入
    // ════════════════════════════════════════════

    /// <summary>
    /// 解析 CSV 文本为点位列表。首行为列头。
    /// 必填列：Name, Address, DataType
    /// 可选列：Access, Enabled, ScanIntervalMs, Deadband, ScaleFactor, ScaleOffset, Description
    /// </summary>
    public OperationResult<IReadOnlyList<DevicePoint>> ParseCsv(string csvText)
    {
        var rows = ParseCsvRows(csvText);
        if (rows.Count < 2)
            return OperationalError.Validation("CSV 至少需要包含列头和数据行");

        var headers = rows[0].Select(h => h.Trim()).ToArray();
        var nameIdx = IndexOf(headers, "Name");
        var addrIdx = IndexOf(headers, "Address");
        var typeIdx = IndexOf(headers, "DataType");
        var accessIdx = IndexOf(headers, "Access");
        var enabledIdx = IndexOf(headers, "Enabled");
        var scanIdx = IndexOf(headers, "ScanIntervalMs");
        var deadIdx = IndexOf(headers, "Deadband");
        var scaleFIdx = IndexOf(headers, "ScaleFactor");
        var scaleOIdx = IndexOf(headers, "ScaleOffset");
        var descIdx = IndexOf(headers, "Description");

        if (nameIdx < 0 || addrIdx < 0 || typeIdx < 0)
            return OperationalError.Validation("CSV 缺少必填列：Name, Address, DataType");

        var points = new List<DevicePoint>();
        for (var r = 1; r < rows.Count; r++)
        {
            var cols = rows[r];
            if (cols.Length < headers.Length)
            {
                _logger.LogWarning("CSV 第 {Line} 行字段数不足（{Cols}/{Headers}），已跳过", r + 1, cols.Length, headers.Length);
                continue;
            }

            var typeStr = cols[typeIdx].Trim();
            if (!Enum.TryParse<DataType>(typeStr, true, out var dataType))
            {
                _logger.LogWarning("CSV 第 {Line} 行: 无法解析 DataType '{Type}'", r + 1, typeStr);
                continue;
            }

            var point = new DevicePoint
            {
                Id = Guid.NewGuid(),
                Name = cols[nameIdx].Trim(),
                Address = cols[addrIdx].Trim(),
                DataType = dataType,
                Access = accessIdx >= 0 && Enum.TryParse<PointAccess>(cols[accessIdx].Trim(), true, out var acc) ? acc : PointAccess.ReadOnly,
                Enabled = enabledIdx < 0 || !bool.TryParse(cols[enabledIdx].Trim(), out var en) || en,
                ScanIntervalMs = scanIdx >= 0 && int.TryParse(cols[scanIdx].Trim(), out var si) ? si : 0,
                Deadband = deadIdx >= 0 && double.TryParse(cols[deadIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var db) ? db : 0,
                ScaleFactor = scaleFIdx >= 0 && double.TryParse(cols[scaleFIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sf) ? sf : 1,
                ScaleOffset = scaleOIdx >= 0 && double.TryParse(cols[scaleOIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var so) ? so : 0,
                Description = descIdx >= 0 ? cols[descIdx].Trim() : null
            };

            points.Add(point);
        }

        _logger.LogInformation("CSV 解析完成: {Count} 个点位", points.Count);
        return points;
    }

    /// <summary>
    /// 解析 CSV 文本为行 × 字段矩阵。支持引号包裹字段：字段内逗号、换行与双引号（"" 转义）。
    /// 空行忽略。
    /// </summary>
    private static List<string[]> ParseCsvRows(string csvText)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        void EndField()
        {
            fields.Add(sb.ToString());
            sb.Clear();
            hasContent = false;
        }

        void EndRow()
        {
            EndField();
            if (fields.Count > 1 || fields[0].Length > 0)
                rows.Add(fields.ToArray());
            fields.Clear();
        }

        for (var i = 0; i < csvText.Length; i++)
        {
            var ch = csvText[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < csvText.Length && csvText[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else
            {
                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        hasContent = true;
                        break;
                    case ',':
                        EndField();
                        break;
                    case '\r':
                    case '\n':
                        EndRow();
                        if (ch == '\r' && i + 1 < csvText.Length && csvText[i + 1] == '\n')
                            i++;
                        break;
                    default:
                        sb.Append(ch);
                        hasContent = true;
                        break;
                }
            }
        }

        if (hasContent || fields.Count > 0 || sb.Length > 0)
            EndRow();

        return rows;
    }

    // ════════════════════════════════════════════
    //  CSV 导出
    // ════════════════════════════════════════════

    /// <summary>将点位列表导出为 CSV 文本</summary>
    public string ExportCsv(IReadOnlyList<DevicePoint> points)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Name,Address,DataType,Access,Enabled,ScanIntervalMs,Deadband,ScaleFactor,ScaleOffset,Description");

        foreach (var p in points)
        {
            sb.AppendLine(string.Join(",",
                EscapeCsv(p.Name),
                p.Address,
                p.DataType.ToString(),
                p.Access.ToString(),
                p.Enabled.ToString(),
                p.ScanIntervalMs.ToString(),
                p.Deadband.ToString(CultureInfo.InvariantCulture),
                p.ScaleFactor.ToString(CultureInfo.InvariantCulture),
                p.ScaleOffset.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(p.Description ?? "")));
        }

        return sb.ToString();
    }

    // ════════════════════════════════════════════
    //  地址自动递增
    // ════════════════════════════════════════════

    /// <summary>
    /// 根据起始地址和数据量生成点位列表。Modbus 地址按 DataType.RegisterCount（寄存器）递增，
    /// S7 地址（DB{n}.DBD/DBW/DBB{offset}）按 DataType.ByteSize（字节）递增（ADR-024 P3-3）。
    /// 名称模板支持占位符：{name}_{###} → {name}_001, {name}_002...
    ///                               {name}_{000} → {name}_000, {name}_001... (零填充)
    /// </summary>
    /// <param name="deviceId">所属设备</param>
    /// <param name="nameTemplate">名称模板，### 替换为序号（零填充）</param>
    /// <param name="startAddress">起始地址（Modbus 为数字如 "40001"；S7 为 "DB1.DBD0" 等）</param>
    /// <param name="count">生成数量</param>
    /// <param name="dataType">数据类型</param>
    /// <param name="access">读写权限</param>
    /// <param name="protocol">协议名（Modbus / S7），决定地址解释与步长</param>
    public IReadOnlyList<DevicePoint> Generate(
        Guid deviceId,
        string nameTemplate,
        string startAddress,
        int count,
        DataType dataType,
        PointAccess access = PointAccess.ReadOnly,
        string protocol = "Modbus")
    {
        if (count <= 0) return Array.Empty<DevicePoint>();
        if (count > 5000) count = 5000;   // 安全上限

        Func<int, int, string> format;
        int step;
        if (protocol.Equals("S7", StringComparison.OrdinalIgnoreCase))
        {
            var start = S7Start.Parse(startAddress, dataType);
            format = start.Format;
            step = dataType.ByteSize();
        }
        else
        {
            var start = ModbusStart.Parse(startAddress);
            format = start.Format;
            step = dataType.RegisterCount();
        }
        var points = new List<DevicePoint>(count);
        var padLen = CountPlaceholders(nameTemplate);  // ### → 3, 000 → 3

        for (var i = 0; i < count; i++)
        {
            points.Add(new DevicePoint
            {
                Id = Guid.NewGuid(),
                Name = ReplacePlaceholders(nameTemplate, i + 1, padLen),
                Address = format(i, step),
                DataType = dataType,
                Access = access,
                Enabled = true
            });
        }

        _logger.LogInformation("批量生成 {Count} 个点位，起始地址 {Addr}，步长 {Step}（协议 {Protocol}）",
            count, startAddress, step, protocol);

        return points;
    }

    // ════════════════════════════════════════════
    //  内部
    // ════════════════════════════════════════════

    private static int IndexOf(string[] headers, string name)
    {
        for (var i = 0; i < headers.Length; i++)
            if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static int CountPlaceholders(string template)
    {
        // 找最长的连续 # 序列长度
        var max = 0;
        var cur = 0;
        foreach (var c in template)
        {
            if (c == '#') { cur++; max = Math.Max(max, cur); }
            else cur = 0;
        }
        return max;
    }

    private static string ReplacePlaceholders(string template, int value, int padLen)
    {
        if (padLen == 0) return template;

        // 优先查找 {###} 模式（花括号包裹的占位符）
        var hashStr = new string('#', padLen);
        var braced = "{" + hashStr + "}";
        var idx = template.IndexOf(braced, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var replaced = value.ToString().PadLeft(padLen, '0');
            return template[..idx] + replaced + template[(idx + braced.Length)..];
        }

        // 回退：裸 ### 模式
        var placeholder = new string('#', padLen);
        idx = template.IndexOf(placeholder, StringComparison.Ordinal);
        if (idx < 0) return template;

        var repl = value.ToString().PadLeft(padLen, '0');
        return template[..idx] + repl + template[(idx + padLen)..];
    }

    /// <summary>Modbus 起始地址解析：纯数字字符串，非法抛 ArgumentException（ADR-024 P3-3）</summary>
    private readonly record struct ModbusStart(int Value)
    {
        public static ModbusStart Parse(string raw) =>
            int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0
                ? new ModbusStart(v)
                : throw new ArgumentException($"无效的 Modbus 起始地址: {raw}（需为非负整数，如 40001）");

        /// <summary>第 i 个点位的地址：起始值 + i*寄存器步长</summary>
        public string Format(int index, int step) => (Value + index * step).ToString();
    }

    /// <summary>S7 DB 区起始地址解析：DB{n}.DBD/DBW/DBB{offset}，按字节步长递增（ADR-024 P3-3）</summary>
    private sealed class S7Start
    {
        private readonly int _db;
        private readonly string _type;
        private readonly int _offset;

        private S7Start(int db, string type, int offset)
        {
            _db = db;
            _type = type;
            _offset = offset;
        }

        public static S7Start Parse(string raw, DataType dataType)
        {
            if (dataType == DataType.Bool)
                throw new ArgumentException("S7 批量生成暂不支持 Bool 位地址（位步进易错，请手动添加）");

            var m = Regex.Match(raw, @"^DB(\d+)\.DB([BDW])(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
                throw new ArgumentException($"无效的 S7 起始地址: {raw}（需为 DB 区地址，如 DB1.DBD0）");

            var type = "DB" + m.Groups[2].Value.ToUpperInvariant();
            var compatible = type switch
            {
                "DBB" => dataType is DataType.Byte or DataType.String,
                "DBW" => dataType is DataType.Int16 or DataType.UInt16,
                "DBD" => dataType is DataType.Int32 or DataType.UInt32 or DataType.Int64 or DataType.UInt64 or DataType.Float or DataType.Double,
                _ => false
            };
            if (!compatible)
                throw new ArgumentException($"S7 起始地址类型 {type} 与数据类型 {dataType} 不兼容（如 Int16 用 DBW、Float 用 DBD）");

            return new S7Start(int.Parse(m.Groups[1].Value), type, int.Parse(m.Groups[3].Value));
        }

        /// <summary>第 i 个点位的地址：DB{n}.DB{T}{offset + i*字节宽}，类型保持起始地址类型</summary>
        public string Format(int index, int step) => $"DB{_db}.{_type}{_offset + index * step}";
    }
}







