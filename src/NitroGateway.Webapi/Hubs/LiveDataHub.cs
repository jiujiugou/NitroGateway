using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace NitroGateway.Webapi.Hubs;

/// <summary>
/// 实时数据推送 Hub。ADR-022 P1-1：必须登录（JWT 经 query string access_token 校验），
/// 订阅前校验 deviceId 为合法 Guid，杜绝匿名订阅与畸形 group 名。
/// </summary>
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
