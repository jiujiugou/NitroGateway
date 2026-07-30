using NitroGateway.Transport.MQTT;
using System;
using System.Collections.Generic;
using System.Text;

namespace NitroGateway.Transport.MQTT
{
    public interface IMqttStateListener
    {
        ValueTask OnStateChangedAsync(
        MqttConnectionState state,
        CancellationToken ct = default);
    }
}
