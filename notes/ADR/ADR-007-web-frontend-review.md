# ADR-007: Web 前端巡检问题清单

- 日期: 2026-08-07
- 状态: 全部条目已处理（2026-08-07）——P1、P2-1~P2-3、P3-1~P3-5 已修复
- 用途: 供后续 agent 直接使用，避免重复扫描；修复后在代码加注释并删除对应条目
- 范围: web/ 全目录（构建验证通过：vue-tsc + vite build 无 TS 错误，仅依赖库 INVALID_ANNOTATION 与 chunk 体积告警）

## 处理记录（2026-08-07）

- P2-1 DashboardView Measurement 回调改取 `m.devicePointId`（对齐 PointSnapshot 字段）
- P2-2 DeviceForm.vue 协议下拉移除 OPC UA / Mitsubishi，仅保留后端已注册的 Modbus + S7
- P2-3 Dashboard/Monitoring 在 DeviceStatusChanged→Online 时补调 SubscribeDevice
- P3-1 删除 TrendChart.vue、stores/deviceStore.ts、getLatest、getDeviceSummary（含 DeviceStatusSummary 类型）
- P3-2 SystemStatus.vue setInterval 改为 onUnmounted 清理（TrendChart 随 P3-1 删除，无残留监听）
- P3-3 DeviceListView.vue handleDel 增加 ElMessageBox 删除确认
- P3-4 index.html 移除 Google Fonts 引用；global.css 字体栈改为系统字体
- P3-5 HistoryView.vue 改 echarts/core 按需引入（Line/Grid/Tooltip/Title/Canvas）；main.ts 移除全量图标注册
