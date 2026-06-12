namespace NitroGateway.Domain.Devices;

/// <summary>点位读写权限</summary>
public enum PointAccess
{
    /// <summary>只读（采集型点位，如传感器读数）</summary>
    ReadOnly,

    /// <summary>只写（控制型点位，如继电器开关）</summary>
    WriteOnly,

    /// <summary>可读写（如变频器频率设定，可读当前值也可下发目标值）</summary>
    ReadWrite
}
