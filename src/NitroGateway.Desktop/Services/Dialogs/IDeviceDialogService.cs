using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>
/// 设备/点位编辑对话框抽象（ADR-029 P4）。
/// ViewModel 依赖本接口而非 Window，便于单测用 fake 替身；
/// WPF 实现为模态 Window（见 <see cref="DeviceDialogService"/>）。
/// </summary>
public interface IDeviceDialogService
{
    /// <summary>编辑设备表单（新建/编辑共用）。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditDevice(DeviceEditor editor);

    /// <summary>编辑点位表单。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditPoint(PointEditor editor);

    /// <summary>批量生成点位表单（docs/13）。返回 true 表示用户点生成且 editor 已更新；false 表示取消</summary>
    bool EditPointBatch(PointBatchEditor editor);

    /// <summary>破坏性操作确认（如删除设备/点位）</summary>
    bool Confirm(string title, string message);

    /// <summary>打开设备点位管理窗口（模态，内部自建 PointsViewModel）。protocolName 用于点位表单/批量生成的协议感知提示</summary>
    void ShowPoints(Guid deviceId, string deviceName, string protocolName);
}
