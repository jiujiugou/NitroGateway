# ADR-055: web 边缘能力缺口——实时监控无曲线 + 点位 CSV 导入未接线

- 日期: 2026-08-18 | 状态: ✅ 已实施（前端缺口补齐，见 2026-08-18 worklog） | 关联: ADR-054（web 单身份化）、F-06（CSV 点位）、F-43（桌面实时曲线）

## 一句话结论

以"完整独立边缘"为标准逐项对比 web vs 桌面，**web 只差两处**：① 实时监控无曲线；② 点位 CSV 导入前端未接线。其余要么对等、要么 web 反超（死信管理 / 系统状态 / 仪表盘）。

## 缺口 1：实时监控无曲线

- 桌面 `RealtimeView.xaml` + `RealtimeViewModel.cs`：LiveCharts2 实时曲线（预载 2h + 环形缓冲）。
- web `MonitoringView.vue`：只有点位数值卡片网格，**无 ECharts 曲线**。
- 修复方向：Monitoring 页加"点选点位 → ECharts 滚动曲线"，数据源 = 现有 SignalR `Measurement` 推送；
  `HistoryView.vue` 已有 echarts 按需引入（Line + step:'end'）可复用。
### 实施（2026-08-18）

- `MonitoringView.vue`：新增「实时曲线」卡片——设备/点位下拉 + ECharts 滚动曲线（按需引入 Line + step:'end'，复用 HistoryView 模式）。
- 选中点位预载最近 2h 历史（`getHistory` limit=1000，对齐桌面 LoadPointHistoryAsync）；SignalR `Measurement` 命中选中点位时环形缓冲追加（上限 7200 点，对齐桌面 MaxChartPoints），500ms 重绘节流。
- 下拉只列启用点位，非数值点位不上曲线；站点过滤语义沿用现状（选中具体站点时实时推送暂停，预载历史仍可用）。

## 缺口 2：点位 CSV 导入未接线

- 后端：`PointImportController.ImportCsv`（POST `/api/devices/{deviceId}/points/import`，body 为 CSV 文本）**已实现**。
- 前端：`web/src/api/devices.ts` 只有 `generatePoints` / `exportPoints`，**无 `importPoints`**；
  `PointList.vue` 只有"导出 CSV / 批量生成"，**无导入按钮**。
- 桌面对照：`PointsViewModel` + `CsvFileService` 有导入导出。
- 修复方向：`devices.ts` 加 `importPoints` + `PointList.vue` 加"⬆ 导入 CSV"（复用导出按钮同排）。
### 实施（2026-08-18）

- `devices.ts`：新增 `importPoints`（POST `/api/devices/{deviceId}/points/import`，`JSON.stringify(csvText)` + application/json，与既有 `updateDeviceStatus` 同款 `[FromBody] string` 编码）。
- `PointList.vue`：新增「⬆ 导入 CSV」按钮（tooltip 提示列头）+ 隐藏 file input；导入成功 ElMessage 显示数量并刷新列表，后端错误信息透出；CSV 读取带 UTF-8/GBK 回退 + BOM 去除（中文 Excel 常导出 GBK）。

## 已核对（非缺口）

- 历史 CSV 导出：桌面 `HistoryViewModel` 也没有 → 两边都缺，不算 web 独缺。
- 设备连接测试 / 串口枚举：web `DeviceForm` + `SystemStatus` 已有 ✓。
- 点位批量生成 / 点位 CSV 导出：web `PointList` 已有 ✓。
- 死信管理 / 系统状态（熔断 / 健康 / 转发 / 串口）：web 反超桌面 ✓。
