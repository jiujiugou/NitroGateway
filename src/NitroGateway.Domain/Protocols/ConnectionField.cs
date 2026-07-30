namespace NitroGateway.Domain.Protocols;

/// <summary>协议连接参数字段定义</summary>
public sealed record ConnectionField
{
    /// <summary>参数 key，如 "Host", "Port", "UnitId", "Rack"</summary>
    public required string Key { get; init; }

    /// <summary>前端显示标签</summary>
    public required string Label { get; init; }

    /// <summary>控件类型: text, number, select</summary>
    public string Type { get; init; } = "text";

    /// <summary>占位提示</summary>
    public string? Placeholder { get; init; }

    /// <summary>默认值</summary>
    public object? Default { get; init; }

    /// <summary>select 类型的选项列表</summary>
    public string[]? Options { get; init; }

    /// <summary>是否必填</summary>
    public bool Required { get; init; } = true;
}
