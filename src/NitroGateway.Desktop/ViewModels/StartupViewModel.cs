using CommunityToolkit.Mvvm.ComponentModel;

namespace NitroGateway.Desktop.ViewModels;

/// <summary>
/// 启动反馈窗口 ViewModel（ADR-037 S8）：状态文案 + 失败标志。
/// 失败时隐藏进度条、显示关闭按钮、文案置红——由 <see cref="IsFailed"/> 绑定驱动，
/// 窗口 code-behind 不再直接操控件。
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    /// <summary>主状态文案（默认启动中；失败时含错误信息）。</summary>
    [ObservableProperty]
    private string _statusText = "正在初始化数据库与后台服务…";

    /// <summary>是否启动失败：失败时进度条隐藏、关闭按钮显示、文案置红。</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>写入启动失败信息（错误文案 + 失败态，由 App 在宿主启动异常时调用）。</summary>
    public void ShowError(string message)
    {
        StatusText = $"启动失败：{message}";
        IsFailed = true;
    }
}
