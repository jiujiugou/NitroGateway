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
