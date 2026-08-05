# [002] 补充 CI 工作流（tasks — 执行清单）

## 前置

- [ ] plan 已按闸门确认（G2 默认通过, 无反对; 规则见 AGENTS.md）

## 任务

- [ ] T-1 核对 ci.yml: 触发条件（branch/PR）、步骤（restore → build → test）、失败即拦截
- [ ] T-2 推送触发 CI, 观察 GitHub Actions 构建+测试结果（→ AC-1/AC-2）
- [ ] T-3 结果回填: 验收勾选 + worklog 记录 CI 报告地址（→ AC-3）

## 执行记录

- 2026-08-05: ci.yml 已建立（构建+测试, 测试改为跑 slnx）, 待推送后 V1 验证
- (后续执行在此追加命令与结果)

## 验收

- [ ] AC-1~AC-3 全部勾选（见 spec.md）
- [ ] 验证命令与结果: `…` → 通过
- [ ] backlog 已标记 [x]
