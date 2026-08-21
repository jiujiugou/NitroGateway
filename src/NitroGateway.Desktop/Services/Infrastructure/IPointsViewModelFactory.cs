using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// 点位窗口 ViewModel 工厂（ADR-029 P2）：把「手工 new PointsViewModel + 逐个
/// GetRequiredService」从 DeviceDialogService 收敛到一处，对话框只依赖本接口。
/// </summary>
public interface IPointsViewModelFactory
{
    /// <summary>创建设备点位管理 ViewModel（内部按 scope 解析依赖）。protocolName 用于点位/批量生成的协议感知。</summary>
    PointsViewModel Create(Guid deviceId, string deviceName, string protocolName);
}
