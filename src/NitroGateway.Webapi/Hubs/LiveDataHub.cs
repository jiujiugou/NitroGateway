using Microsoft.AspNetCore.SignalR;

namespace NitroGateway.Webapi.Hubs;

public class LiveDataHub : Hub
{
    public async Task SubscribeDevice(string deviceId) => await Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    public async Task UnsubscribeDevice(string deviceId) => await Groups.RemoveFromGroupAsync(Context.ConnectionId, deviceId);
}
