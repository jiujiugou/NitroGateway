namespace NitroGateway.Persistence;

/// <summary>
/// 点位 EF 实体，映射 points 表（PascalCase 列名，见 M003 迁移的历史命名说明）。
/// 与领域模型 DevicePoint 通过 <see cref=\ DomainMapper\/> 双向转换；
/// 枚举以字符串存储，采集参数以数值列存储。
/// </summary>
public sealed class PointEntity
{
    /// <summary>点位唯一标识（GUID），主键</summary>
    public Guid Id { get; set; }

    /// <summary>所属设备 ID，外键关联 devices.Id，删除设备时级联删除</summary>
    public Guid DeviceId { get; set; }

    /// <summary>点位名称，必填，最长 200 字符</summary>
    public required string Name { get; set; }

    /// <summary>协议寄存器/内存地址（如 Modbus 地址串），必填，最长 200 字符</summary>
    public required string Address { get; set; }

    /// <summary>点位描述，可空</summary>
    public string? Description { get; set; }

    /// <summary>数据类型，DataType 枚举字符串，必填，最长 50 字符</summary>
    public required string DataType { get; set; }

    /// <summary>读写权限，PointAccess 枚举字符串（ReadOnly/ReadWrite），必填，最长 50 字符</summary>
    public required string Access { get; set; }

    /// <summary>是否参与采集，默认 true</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>独立采集间隔（毫秒），0 表示跟随设备默认周期；负数视为非法</summary>
    public int ScanIntervalMs { get; set; }

    /// <summary>变化死区阈值，与上次写入值的差小于该值时跳过写入，用于过滤小幅波动；0 表示不过滤</summary>
    public double Deadband { get; set; }

    /// <summary>工程量缩放系数，默认 1.0（value = raw × ScaleFactor + ScaleOffset）</summary>
    public double ScaleFactor { get; set; } = 1.0;

    /// <summary>工程量缩放偏移，默认 0</summary>
    public double ScaleOffset { get; set; }

    /// <summary>写值范围下限（null = 不限），供 WriteGuard.Range 校验（M013）</summary>
    public double? MinLimit { get; set; }

    /// <summary>写值范围上限（null = 不限），供 WriteGuard.Range 校验（M013）</summary>
    public double? MaxLimit { get; set; }

    /// <summary>配置最后修改时间（O 格式 UTC 字符串，ADR-033 阶段 3/4；空串=最旧）</summary>
    public string UpdatedAt { get; set; } = "";

    /// <summary>删除标记（tombstone，ADR-033 阶段 3/4；中心侧权威删除）</summary>
    public bool IsDeleted { get; set; }

    /// <summary>所属设备导航属性（对应 <see cref=\DeviceEntity.Points\/>）</summary>
    public DeviceEntity Device { get; set; } = null!;
}
