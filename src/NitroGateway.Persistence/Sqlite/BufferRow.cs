using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Persistence.Sqlite
{
    internal sealed class BufferRow
    {
        public string Id { get; set; } = default!;
        public string Payload { get; set; } = default!;
    }
}
