using Microsoft.AspNetCore.SignalR;

namespace NitroGateway.Webapi.Hubs;

public class LiveDataHub : Hub
{
    public async Task SubscribeDevice(string deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    }

    public Task UnsubscribeDevice(string deviceId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, deviceId);
}
