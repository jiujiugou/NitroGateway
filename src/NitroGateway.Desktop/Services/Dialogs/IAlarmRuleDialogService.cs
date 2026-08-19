using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>
/// 告警规则编辑对话框抽象（ADR-043）。
/// ViewModel 依赖本接口而非 Window，便于单测用 fake 替身；
/// WPF 实现为模态 Window（见 <see cref="AlarmRuleDialogService"/>）。
/// </summary>
public interface IAlarmRuleDialogService
{
    /// <summary>编辑告警规则表单（新建/编辑共用）。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditRule(AlarmRuleEditor editor);

    /// <summary>破坏性操作确认（如删除告警规则）</summary>
    bool Confirm(string title, string message);
}
