using System.Windows;
using System.Windows.Controls;
using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Views;

/// <summary>设置页 code-behind：中心 Token 与 MQTT 密码的遮蔽/显示切换（ADR-037 S5 / ADR-067）。</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>ViewModel 就位时把已保存 Token 回填遮蔽框。</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SettingsViewModel vm)
        {
            //TokenMaskedBox.Password = vm.CenterToken;
            MqttPasswordMaskedBox.Password = vm.MqttPassword;
        }
    }

    /// <summary>遮蔽输入框变化时同步明文回 ViewModel（仅内存）。</summary>
    /*private void OnCenterTokenPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.CenterToken = TokenMaskedBox.Password;
    }*/

    /// <summary>勾选后切到明文输入框并回填当前值。</summary>
    /*
    private void OnTokenRevealChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            TokenVisibleBox.Text = vm.CenterToken;
            TokenVisibleBox.Visibility = Visibility.Visible;
            TokenMaskedBox.Visibility = Visibility.Collapsed;
        }
    }
    */
    /// <summary>取消勾选后切回遮蔽框并回填当前值。</summary>
    /*
    private void OnTokenRevealUnchecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            TokenMaskedBox.Password = vm.CenterToken;
            TokenMaskedBox.Visibility = Visibility.Visible;
            TokenVisibleBox.Visibility = Visibility.Collapsed;
        }
    }
    */
    /// <summary>遮蔽输入框变化时同步明文回 ViewModel（仅内存）。</summary>
    private void OnMqttPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.MqttPassword = MqttPasswordMaskedBox.Password;
    }

    /// <summary>勾选后切到明文输入框并回填当前值。</summary>
    private void OnMqttPasswordRevealChecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            MqttPasswordVisibleBox.Text = vm.MqttPassword;
            MqttPasswordVisibleBox.Visibility = Visibility.Visible;
            MqttPasswordMaskedBox.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>取消勾选后切回遮蔽框并回填当前值。</summary>
    private void OnMqttPasswordRevealUnchecked(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            MqttPasswordMaskedBox.Password = vm.MqttPassword;
            MqttPasswordMaskedBox.Visibility = Visibility.Visible;
            MqttPasswordVisibleBox.Visibility = Visibility.Collapsed;
        }
    }
}
