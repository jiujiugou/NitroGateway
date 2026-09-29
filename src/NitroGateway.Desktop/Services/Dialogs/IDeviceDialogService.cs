using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Dialogs;

public interface IDeviceDialogService
{
    /// <summary>编辑设备表单（新建/编辑共用）。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditDevice(DeviceEditor editor);

    /// <summary>编辑点位表单。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditPoint(PointEditor editor);

    /// <summary>
    /// 编辑 OPC UA 点位（左=服务器地址空间浏览树，右=点位表单，ADR-070 层次 1）。
    /// 返回 true 表示用户点保存且 editor 已更新；false 表示取消。
    /// </summary>
    bool EditOpcUaPoint(Guid deviceId, PointEditor editor);

    /// <summary>批量生成点位表单（docs/13）。返回 true 表示用户点生成且 editor 已更新；false 表示取消</summary>
    bool EditPointBatch(PointBatchEditor editor);

    /// <summary>写值对话框（下发控制值）。返回 true 表示用户确认下发且 editor.InputValue 已填；false 表示取消</summary>
    bool EditWrite(WriteValueEditor editor);

    /// <summary>破坏性操作确认（如删除设备/点位）</summary>
    bool Confirm(string title, string message);

    /// <summary>打开设备点位管理窗口（模态，内部自建 PointsViewModel）。protocolName 用于点位表单/批量生成的协议感知提示</summary>
    void ShowPoints(Guid deviceId, string deviceName, string protocolName);
}
