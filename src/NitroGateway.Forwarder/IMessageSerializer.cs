using NitroGateway.Domain.Measurements;

namespace NitroGateway.Forwarder;

/// <summary>
/// 消息序列化：BatchMeasurements → 发布负载字节。
/// <para>
/// 与传输层解耦——Forwarder 只依赖该接口产生负载，具体格式（JSON/Protobuf/压缩）由实现决定，
/// 便于 v2+ 演进（见模块 DESIGN.md）。
/// </para>
/// </summary>
public interface IMessageSerializer
{
    /// <summary>把一个测量批次序列化为 MQTT 发布负载</summary>
    /// <param name="batch">待序列化的测量批次（含设备 ID、点位测量等）</param>
    /// <returns>序列化后的负载字节；MQTT 发布时作为消息体发送</returns>
    byte[] Serialize(BatchMeasurements batch);

    /// <summary>序列化产物的 MIME 类型，如 "application/json"，供消费端识别格式</summary>
    string ContentType { get; }
}
