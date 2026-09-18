using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Infrastructure;

public interface IPointsViewModelFactory
{
    /// <summary>创建设备点位管理 ViewModel（内部按 scope 解析依赖）。protocolName 用于点位/批量生成的协议感知。</summary>
    PointsViewModel Create(Guid deviceId, string deviceName, string protocolName);
}
