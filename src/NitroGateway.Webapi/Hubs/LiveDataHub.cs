using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NitroGateway.Webapi.Hubs;

[Authorize]
public class LiveDataHub : Hub
{
    public async Task SubscribeDevice(string deviceId)
    {
        if (!Guid.TryParse(deviceId, out _))
            throw new HubException("deviceId 必须是合法的 Guid");
        await Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    }

    public Task UnsubscribeDevice(string deviceId)
        => Guid.TryParse(deviceId, out _)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, deviceId)
            : Task.CompletedTask;
}
