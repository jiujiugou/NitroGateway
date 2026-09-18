using CommunityToolkit.Mvvm.ComponentModel;

namespace NitroGateway.Desktop.ViewModels;

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
