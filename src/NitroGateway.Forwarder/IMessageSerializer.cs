using NitroGateway.Domain.Measurements;

namespace NitroGateway.Forwarder;

/// <summary>消息序列化。BatchMeasurements → byte[]</summary>
public interface IMessageSerializer
{
    /// <summary>序列化一个批次</summary>
    byte[] Serialize(BatchMeasurements batch);

    /// <summary>MIME 类型，如 "application/json"</summary>
    string ContentType { get; }
}
