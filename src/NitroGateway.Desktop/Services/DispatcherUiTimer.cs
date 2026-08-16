using System.Windows.Threading;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// WPF <see cref="DispatcherTimer"/> 实现的 <see cref="IUiTimer"/>（UI 线程周期回调）。
/// 周期长短由调用方决定（设备/告警页 5s 轮询），本类只负责把 DispatcherTimer 包成可替换接口。
/// </summary>
public sealed class DispatcherUiTimer : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherUiTimer(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Tick;

    /// <inheritdoc />
    public void Start() => _timer.Start();

    /// <inheritdoc />
    public void Stop() => _timer.Stop();
}
