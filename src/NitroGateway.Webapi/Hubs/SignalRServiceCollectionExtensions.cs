using System.Threading.Channels;
using NitroGateway.DeviceManagement.Events;
using NitroGateway.Domain.Events;

namespace NitroGateway.Webapi.Hubs;

// ═══════════ DI 注册 ═══════════

/// <summary>SignalR 推送 DI 注册</summary>
public static class SignalRServiceCollectionExtensions
{
    /// <summary>注册 SignalR 统一出口（Channel + Dispatcher + Consumer）</summary>
    public static IServiceCollection AddNitroSignalR(this IServiceCollection services)
    {
        var channel = Channel.CreateBounded<OutboxMessage>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        services.AddSingleton(channel);
        services.AddSingleton<SignalRDispatcher>();
        services.AddSingleton<IPointStoredSink>(sp => sp.GetRequiredService<SignalRDispatcher>());
        services.AddSingleton<IDeviceHealthListener>(sp => sp.GetRequiredService<SignalRDispatcher>());
        services.AddHostedService<OutboxConsumer>();

        return services;
    }
}
