# ADR-043: 桌面端告警规则管理——按设备/点位配置「条件触发报警」

- 日期: 2026-08-13 | 状态: 已实施
- 来源: 用户澄清「我想要的是那种可以为设备设置，到达什么条件后就触发报警那种」
- 关联: ADR-026（桌面壳）、ADR-029（桌面配置对话框）

## Context

桌面现场采集端已有「告警」页，但它只是展示 AlarmRule 触发后的告警结果（AlarmsViewModel 查 IAlarmRepository），没有任何规则配置入口——不能为设备/点位设置「值达到 X 就报警」。规则配置此前只存在于 Web 端；桌面作为现场主操作面、常单机离线运行，现场人员无法配置告警条件。范围纠正：早前曾把「异常报警」误实现为设备离线/通信异常事件记录（M013 + AbnormalAlarmRecorder 已回退），并非用户所需。

## Decision

- D1 桌面新增「告警规则」导航页：列表展示全部规则（含禁用），设备/点位/条件（运算符+阈值）/持续时长/严重等级/启用/操作；新增/编辑走模态对话框 AlarmRuleEditorWindow（设备→点位级联、运算符 > >= < <= == != Between、阈值（Between 显上下限）、持续时长、严重等级、消息模板、启用）。
- D2 持久化复用 IAlarmRuleRepository（Scoped，IServiceScopeFactory 每次操作建 scope，与 AlarmsViewModel 同模式）；CachedAlarmRuleRepository.Save/Delete 成功即失效缓存，桌面与 AlarmHostedService 同进程，新规则立即生效评估。
- D3 IAlarmRuleRepository 新增只读方法 GetAllIncludingDisabledAsync（含禁用、绕过缓存直读内层，管理页低频调用）；评估热路径仍用 GetAllAsync（仅启用、走缓存）。接口只增不删，Web 侧零改动。

## Alternatives

- 规则配置只在 Web 端：现场离线无法配置告警条件，主操作面缺失。
- 用设备离线/通信异常事件记录代替规则告警：非用户所需，已回退。

## Rationale

桌面是现场主操作面且常单机离线运行，规则配置入口必须在桌面端；复用现有 AlarmRule/AlarmEvaluator 系统避免重复造轮子；接口只增不删保证 Web 与第三方兼容。

## Consequences

- 现场可配置「条件触发报警」并立即生效评估；管理页可展示/恢复禁用规则。
- Web 侧零改动；评估热路径不受影响。
