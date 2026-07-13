using System.Globalization;
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
        var lines = csvText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
            return OperationalError.Validation("CSV 至少需要包含列头和数据行");

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
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
        for (var i = 1; i < lines.Length; i++)
        {
            var cols = lines[i].Split(',');
            if (cols.Length < headers.Length) continue;

            var typeStr = cols[typeIdx].Trim();
            if (!Enum.TryParse<DataType>(typeStr, true, out var dataType))
            {
                _logger.LogWarning("CSV 行 {Line}: 无法解析 DataType '{Type}'", i + 1, typeStr);
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
    /// 根据起始地址和数据量生成点位列表。地址按 DataType.RegisterCount 递增。
    /// 名称模板支持占位符：{name}_{###} → {name}_001, {name}_002...
    ///                               {name}_{000} → {name}_000, {name}_001... (零填充)
    /// </summary>
    /// <param name="deviceId">所属设备</param>
    /// <param name="nameTemplate">名称模板，### 替换为序号（零填充）</param>
    /// <param name="startAddress">起始地址</param>
    /// <param name="count">生成数量</param>
    /// <param name="dataType">数据类型</param>
    /// <param name="access">读写权限</param>
    public IReadOnlyList<DevicePoint> Generate(
        Guid deviceId,
        string nameTemplate,
        int startAddress,
        int count,
        DataType dataType,
        PointAccess access = PointAccess.ReadOnly)
    {
        if (count <= 0) return Array.Empty<DevicePoint>();
        if (count > 5000) count = 5000;   // 安全上限

        var step = dataType.RegisterCount();
        var points = new List<DevicePoint>(count);
        var padLen = CountPlaceholders(nameTemplate);  // ### → 3, 000 → 3

        for (var i = 0; i < count; i++)
        {
            points.Add(new DevicePoint
            {
                Id = Guid.NewGuid(),
                Name = ReplacePlaceholders(nameTemplate, i + 1, padLen),
                Address = (startAddress + i * step).ToString(),
                DataType = dataType,
                Access = access,
                Enabled = true
            });
        }

        _logger.LogInformation("批量生成 {Count} 个点位，起始地址 {Addr}，步长 {Step}",
            count, startAddress, step);

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
}
