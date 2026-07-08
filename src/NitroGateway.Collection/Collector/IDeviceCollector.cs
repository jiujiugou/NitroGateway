using NitroGateway.Domain.Devices;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Collection
{
    public interface IDeviceCollector
    {
        Task CollectOnceAsync(CancellationToken ct);

        Task CollectDeviceAsync(Device device, CancellationToken ct);
    }
}
