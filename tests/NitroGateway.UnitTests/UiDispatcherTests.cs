using System.Windows.Threading;
using NitroGateway.Desktop.Services;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-027 P3-2：UiDispatcher 在 Dispatcher 关闭后入队不得抛异常
/// （EventBridge 帧循环在应用关闭期间仍在后台触发）。
/// </summary>
public sealed class UiDispatcherTests
{
    [Fact]
    public void TryBeginInvoke_after_dispatcher_shutdown_does_not_throw()
    {
        Exception? error = null;
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run(); // 泵送直到收到关闭请求
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

        dispatcher!.BeginInvokeShutdown(DispatcherPriority.Send);
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        // 与 EventBridge 帧循环一致：应用关闭后从后台线程入队，不得抛异常
        // （.NET 10 关闭后的 BeginInvoke 同步不抛，但 Operation.Task 会以取消结束；
        // 保护覆盖同步异常路径，本断言守护契约不回归）
        try
        {
            UiDispatcher.TryBeginInvoke(dispatcher, () => { });
        }
        catch (Exception ex)
        {
            error = ex;
        }
        Assert.Null(error);
    }

}
