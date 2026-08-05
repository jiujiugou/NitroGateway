# [002] 补充 CI 工作流（plan — 怎么做）

## 现状（相关代码位置）

- 目标文件: .github/workflows/ci.yml（2026-08-05 已建, 构建+测试, 测试改跑 slnx）
- 验证入口: GitHub Actions（仓库已远程托管）

## 方案

- 收尾 ci.yml: 核对触发条件（branch/PR）、步骤（restore → build → test）、失败即拦截
- 推送触发 CI, 观察 GitHub Actions 运行结果
- 结果回填: 验收勾选 + worklog 记录 CI 报告地址

## 风险与对策

- 风险: Actions 环境与本地环境差异导致红 → 对策: 先本地跑 build+test 确认 0 错再推送
- 风险: 触发条件配置错误导致不运行 → 对策: 核对 branch/PR 触发写法

## 假设（需用户确认）

- [ ] 仓库已远程托管, 可观察 GitHub Actions 运行

## 决策点（需用户拍板）

- [ ] 决策点 1: 无新增技术选型决策点（沿用已建 ci.yml 方案; 推送时机决策点见 spec.md）

## 确认状态

- [ ] 闸门级别: G2（默认通过, 可反对）; 已启动（[s], 2026-08-05）
