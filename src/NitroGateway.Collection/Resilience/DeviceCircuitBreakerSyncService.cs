using Microsoft.Extensions.Hosting;
using NitroGateway.DeviceManagement;
using NitroGateway.Domain.Devices;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Collection.Resilience
{
    public sealed class DeviceCircuitBreakerSyncService : IHostedService
    {
        private readonly IDeviceHealthMonitor _monitor;
        private readonly ICircuitBreakerRegistry _breakers;

        public DeviceCircuitBreakerSyncService(
            IDeviceHealthMonitor monitor,
            ICircuitBreakerRegistry breakers)
        {
            _monitor = monitor;
            _breakers = breakers;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _monitor.StatusChanged += OnThresholdReached;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _monitor.StatusChanged -= OnThresholdReached;
            return Task.CompletedTask;
        }

        private void OnThresholdReached(Guid deviceId, DeviceStatus status)
        {
            if (status == DeviceStatus.Online)
            {
                _breakers.Reset(deviceId);
            }
        }
    }
}
