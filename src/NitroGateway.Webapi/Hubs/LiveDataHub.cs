using Microsoft.AspNetCore.SignalR;

namespace NitroGateway.Webapi.Hubs;

public class LiveDataHub : Hub
{
    public Task SubscribeDevice(string deviceId) => Groups.AddToGroupAsync(Context.ConnectionId, deviceId);
    public Task UnsubscribeDevice(string deviceId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, deviceId);
}
