using NitroGateway.Desktop.ViewModels;
using Xunit;

namespace NitroGateway.UnitTests;

public sealed class StartupViewModelTests
{
    [Fact]
    public void ShowError_sets_failed_flag_and_status_text()
    {
        var vm = new StartupViewModel();

        Assert.False(vm.IsFailed);
        Assert.Equal("正在初始化数据库与后台服务…", vm.StatusText);

        vm.ShowError("数据库迁移失败");

        Assert.True(vm.IsFailed);
        Assert.Contains("数据库迁移失败", vm.StatusText);
        Assert.Contains("启动失败", vm.StatusText);
    }

    [Fact]
    public void Default_status_is_initializing()
    {
        var vm = new StartupViewModel();

        Assert.False(vm.IsFailed);
        Assert.StartsWith("正在初始化", vm.StatusText);
    }
}
