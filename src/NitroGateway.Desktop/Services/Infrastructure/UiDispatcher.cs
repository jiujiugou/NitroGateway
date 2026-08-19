using System.Windows;
using System.Windows.Threading;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// ADR-026 D2：Dispatcher 封装。EventBridge 帧在后台线程产生，
/// ViewModel 经本类把 ObservableCollection / 属性更新贴回 UI 线程。
/// </summary>
public sealed class UiDispatcher
{
    /// <summary>
    /// 在 UI 线程执行动作；无 WPF Application（如测试）或已在 UI 线程时同步执行。
    /// </summary>
    /// <param name="action">要在 UI 线程执行的动作</param>
    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            TryBeginInvoke(dispatcher, action);
    }

    /// <summary>
    /// 关闭安全的 Dispatcher 入队（ADR-027 P3-2）：应用关闭中 Dispatcher 已停止受理
    /// 新操作（BeginInvoke 抛异常），直接丢弃——EventBridge 帧循环仍在后台触发，
    /// 不能让它崩掉；动作本身若有异常由 Dispatcher 未处理异常通道接管，不经由此处。
    /// </summary>
    internal static void TryBeginInvoke(Dispatcher dispatcher, Action action)
    {
        try
        {
            dispatcher.BeginInvoke(action);
        }
        catch (Exception)
        {
            // 应用关闭中，丢弃该次 UI 更新
        }
    }
}
