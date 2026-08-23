using System.Windows;

namespace NitroGateway.Desktop.Views;

/// <summary>
/// 写值输入窗口（仅 UI 骨架，写值业务由调用方实现）。
/// DataContext 需提供：<c>DeviceName / PointName / Address / DataType / CurrentValueText / RangeText / InputValue</c>。
/// </summary>
public partial class WriteValueWindow : Window
{
    public WriteValueWindow()
    {
        InitializeComponent();
    }

    /// <summary>取消：关闭窗口并返回 false</summary>
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 确认：InputValue 已由 TextBox 双向绑定写回 DataContext（WriteValueEditor），
    /// 关闭并返回 true，真正下发由调用方在 EditWrite 返回 true 后执行（docs/14 §5.2）。
    /// </summary>
    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
