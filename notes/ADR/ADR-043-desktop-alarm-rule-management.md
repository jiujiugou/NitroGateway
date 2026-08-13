# ADR-043: 桌面端告警规则管理——按设备/点位配置「条件触发报警」（2026-08-13）

- 日期: 2026-08-13 | 状态: 已实施（2026-08-13） | 来源: 用户澄清「我想要的是那种可以为设备设置，到达什么条件后就触发报警那种」
- 关联: ADR-026（桌面壳）、ADR-032（规则缓存）、ADR-029（桌面配置对话框）、ADR-037（UI 美化）

## 问题
- 桌面现场采集端已有「告警」页，但它只是**展示** `AlarmRule` 触发后的告警结果（`AlarmsViewModel` 查 `IAlarmRepository`），**没有任何规则配置入口**——不能为设备/点位设置「值达到 X 就报警」的条件。
- 规则配置此前只存在于 Web 端（`web/src/views/Alarms/AlarmRulesView.vue` + `AlarmRulesController` → `IAlarmRuleRepository`）；桌面作为现场主操作面、常单机离线运行，现场人员无法配置告警条件。
- 范围纠正：早前曾把「异常报警」误实现为设备离线/通信异常事件记录（M013 + AbnormalAlarmRecorder，2026-08-13 已回退）——并非用户所需；用户要的是「配置规则 → 条件触发报警」，即现有 AlarmRule/AlarmEvaluator 系统在桌面端的配置入口。

## 修复方向（已实施）
- 桌面新增「告警规则」导航页（紧邻「告警」）：`AlarmRulesViewModel` + `AlarmRulesView`。
  - 列表展示**全部**规则（含禁用）：设备 / 点位 / 条件（运算符+阈值）/ 持续时长 / 严重等级 / 启用 / 操作；设备名与点位名经 `IDeviceSnapshotCache`（含点位）映射，回退短 ID。
  - 新增/编辑走模态对话框 `AlarmRuleEditorWindow`（经 `IAlarmRuleDialogService` 抽象，仿 `IDeviceDialogService` 便于单测）：设备→点位级联（`AlarmRuleEditor.Points` 随设备切换）、运算符（`>` `>=` `<` `<=` `==` `!=` `Between`）、阈值（Between 显上下限）、持续时长、严重等级、消息模板、启用。
- 持久化复用 `IAlarmRuleRepository`（Scoped，`IServiceScopeFactory` 每次操作建 scope，与 `AlarmsViewModel` 同模式）；`CachedAlarmRuleRepository.Save/Delete` 成功即失效缓存，桌面与 `AlarmHostedService` 同进程，新规则立即生效评估。
- 为管理页能展示/恢复禁用规则，`IAlarmRuleRepository` 新增只读方法 `GetAllIncludingDisabledAsync`（含禁用、绕过缓存直读内层，管理页低频调用）；评估热路径仍用 `GetAllAsync`（仅启用、走缓存）。接口只增不删，Web 侧零改动。
- 导航项「告警规则」（Segoe MDL2 `\uE8FD` BulletedList）+ `MainWindow` DataTemplate；`DesktopServiceCollectionExtensions` 注册 `IAlarmRuleDialogService` 与 `AlarmRulesViewModel`。
- 验证：新增 `AlarmRuleEditorTests`（8）+ `AlarmRulesViewModelTests`（12）+ `CachedAlarmRuleRepositoryTests` 缓存旁路用例；`DesktopViewSmokeTests` 加入 `AlarmRulesView` 与 `AlarmRuleEditorWindow` 冒烟；构建 0 错误、全量 578 测试通过。

## G1（接口变更）
- `IAlarmRuleRepository` 新增 `GetAllIncludingDisabledAsync`（只增不删）；`SqliteAlarmRuleRepository` / `CachedAlarmRuleRepository`（直透内层）/ `InMemoryAlarmRuleRepository` 三实现同步补齐；不触碰 Storage/Protocol 纯接口。
