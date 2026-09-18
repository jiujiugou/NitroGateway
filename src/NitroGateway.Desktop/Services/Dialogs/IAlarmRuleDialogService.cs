using NitroGateway.Desktop.ViewModels;

namespace NitroGateway.Desktop.Services.Dialogs;

public interface IAlarmRuleDialogService
{
    /// <summary>编辑告警规则表单（新建/编辑共用）。返回 true 表示用户点保存且 editor 已更新；false 表示取消</summary>
    bool EditRule(AlarmRuleEditor editor);

    /// <summary>破坏性操作确认（如删除告警规则）</summary>
    bool Confirm(string title, string message);
}
