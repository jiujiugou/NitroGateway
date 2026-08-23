# ADR-037: 桌面 UI 美化与优化分步计划

- 日期: 2026-08-12 | 状态: 已全部实施（2026-08-13） | 来源: 用户要求「桌面 UI 可美化和优化的地方分步骤写出文档」
- 范围: src/NitroGateway.Desktop（Views + ViewModels + Themes + App），不动后端/契约/依赖
- 三问: 为什么做=桌面端是现场主操作面，当前为纯功能实现、无状态色/空态/校验，交互粗糙；验收=按步骤逐项落地，每步附测试且 build+单测全绿；不做=体验粗糙与维护成本（散落硬编码色值、全量重建列表）持续累积

## 实施记录（2026-08-13，详见 notes/worklog/2026-08-13.md）

- 阶段 0 体验快赢: S1 色值令牌化（Views 零 # 字面量，主题令牌收拢 Styles.xaml）；S2 设备状态列按 Web DeviceStatusTag 语义色上色；S3 空态叠加层 + 加载进度条/按钮禁用
- 阶段 1 健壮性与表单: S4 DeviceEditor/PointEditor INotifyDataErrorInfo 校验（非法值拦截保存，红框+悬浮提示）；S5 中心 Token PasswordBox 遮蔽 + DPAPI(P/Invoke) 落盘加密 + 旧明文迁移；S6 表单窗口 CanResize + MinWidth/Height + ScrollViewer
- 阶段 2 流畅度与启动: S7 设备/告警列表 diff 增量刷新（行实例原位更新，保留选中/滚动）；S8 StartupWindow 启动反馈（失败窗口内提示）；S9 曲线窗口 7200 点对齐 2h 预载
- 阶段 3 锦上添花: S10 导航 Segoe MDL2 图标 + 品牌渐变；S11 设备数并入 DevicesViewModel 刷新事件（去掉重复查询）；S12 RingObservableCollection.TrimFront 批量移除溢出（单次 Reset 通知）
- 验证: dotnet build NitroGateway.slnx 0 错误；单测 558 全绿（基线 531 + 27）；IntegrationTests 43 全绿；DesktopViewSmokeTests 新增列表视图/启动窗冒烟
- 未提交: git 提交由用户执行
