using NitroGateway.Domain.Devices;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Collection
{
    public interface IDeviceCollector
    {
        public Task CollectDeviceAsync(Device device, CancellationToken ct);
        public Task CollectOnceAsync(CancellationToken ct);
    }
}
