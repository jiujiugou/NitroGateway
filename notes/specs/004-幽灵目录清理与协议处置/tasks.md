# [004] 幽灵目录清理 + Mitsubishi/OpcUa 处置（tasks — 执行清单）

## 前置

- [ ] 闸门级别 G1: 用户已明确确认 Mitsubishi/OpcUa 处置方案（spec.md 决策点）

## 任务

- [ ] T-1 幽灵目录/幽灵引用清理收尾（部分已完成: Scheduler/Infrastructure.Sqlite 残留清理, 随 D-07 构建 0 错）（→ AC-1）
- [ ] T-2 Mitsubishi/OpcUa 处置选项整理并提交用户决策（保留修复/删除/移出）（→ AC-2）
- [ ] T-3 执行用户决策, 确认 slnx 与仓库结构与决策一致（→ AC-3）

## 执行记录

- 2026-08-05: Scheduler/Infrastructure.Sqlite 幽灵目录已清理; Mitsubishi（1 错）/OpcUa（6 错）为不可编译半成品, 处置待用户决策
- (后续执行在此追加命令与结果)

## 验收

- [ ] AC-1~AC-3 全部勾选（见 spec.md）
- [ ] 验证命令与结果: `…` → 通过
- [ ] backlog 已标记 [x]
