using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Infrastructure;

/// <summary>
/// 设备列表 ViewModel 工厂：同一套 <see cref="DevicesViewModel"/> 按协议分区创建多份，
/// 对应 Web 端的「Modbus / S7 设备」与「OPC UA 设备」两个独立页面。
/// </summary>
public interface IDevicesViewModelFactory
{
    /// <summary>按协议分区创建设备列表 ViewModel。</summary>
    DevicesViewModel Create(DeviceListScope scope);
}
