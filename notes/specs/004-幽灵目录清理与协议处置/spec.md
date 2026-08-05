# [004] 幽灵目录清理 + Mitsubishi/OpcUa 处置（spec — 做什么/为什么）

- 对应待办: [D-08] 幽灵目录清理 + Mitsubishi/OpcUa 处置(优化, 中) — notes/backlog.md

## 背景（为什么做）

- 来源: 工程优化（开发发现）
- 需求三问:
  - 为什么做: Scheduler/Infrastructure.Sqlite 仅 obj 残留且被错误引用（曾致 Verification 构建失败）; Mitsubishi/OpcUa 有源码但无归属, 残留与孤立继续混淆认知
  - 验收标准是什么: 见下
  - 不做会怎样: 残留与孤立项目持续混淆认知, 工具链可能再次被幽灵引用破坏

## 现状（2026-08-05）

- Scheduler/Infrastructure.Sqlite 残留已清理（随 D-07 修复 Verification 构建 0 错）
- Mitsubishi（1 错）/OpcUa（6 错）为不可编译半成品, 处置待用户决策

## 目标

- 仓库无幽灵目录/幽灵引用; Mitsubishi/OpcUa 处置（保留修复/删除/移出）有决策记录并执行

## 边界

- 做: 清理收尾 + 协议处置决策与执行
- 不做（Non-Goals）: 不修复 Mitsubishi/OpcUa 半成品（除非用户选择保留修复）; 不新增协议功能

## 验收标准（可测试）

- [ ] AC-1: 幽灵目录/幽灵引用清理完毕, 全仓构建 0 错 — 验证: dotnet build NitroGateway.slnx
- [ ] AC-2: Mitsubishi/OpcUa 处置有决策记录 — 验证: 记录存在
- [ ] AC-3: 决策执行完毕, 仓库结构与决策一致 — 验证: slnx/目录核对

## 决策点（需用户拍板）

- [ ] 决策点 1: Mitsubishi/OpcUa 处置方式三选一: 保留修复 / 删除 / 移出仓库备份

## 工程基线（固定节, 勿删; 不适用项标 N/A）

- [ ] 错误处理: N/A（工程清理类, 无新增运行时代码）
- [ ] 健康检查/可观测: N/A
- [ ] 测试: 清理后全量测试通过（dotnet test）
- [ ] 文档: 决策记录 + 清理结果回写 worklog
