using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Persistence.Sqlite
{
    /// <summary>
    /// 转发缓冲出队用的轻量行投影（仅 id + payload），
    /// 避免把整行（含状态、重试计数等）反序列化进内存；payload 在出队后按需反序列化为 BatchMeasurements。
    /// </summary>
    internal sealed class BufferRow
    {
        /// <summary>批次 ID（GUID 字符串，对应 forward_buffer.id）</summary>
        public string Id { get; set; } = default!;

        /// <summary>BatchMeasurements 的 CamelCase JSON 负载</summary>
        public string Payload { get; set; } = default!;
    }
}
