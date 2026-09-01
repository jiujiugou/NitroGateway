# ADR-045: 桌面实时图表内存与主线程开销决策

- 日期: 2026-08-15 | 状态: 已实施
- 来源: 用户反馈「桌面 UI 绘图吃内存很大」+ dotnet-stack 定位
- 关联: ADR-026（桌面壳）

## Context

实时页唯一图表 CartesianChart 7200 点整量交给 LiveCharts2/SkiaSharp 画，绘制成本 ∝ 点数；FrameReady 事件进程级常驻（每 200ms 一帧）+ 切页不暂停实时页 + 最小化不处理 → 不可见时重绘链照跑；_series 未设 AnimationsSpeed 每帧产生动画对象；LiveCharts2 WPF 切 Visibility/反复切页内存不回收（GitHub #1468/#737），7200 点接近官方 ~10k 上限（#1237）。

## Decision

- D1 生命周期 IsActive：导航切走/窗口最小化 → OnFrame 直接 return + 显示集合清空（_series.Values = null/ChartValues.Clear()），回来重载最近窗口；缓解切页泄漏。
- D2 图表只绑降采样小窗口：VM 保留 2h/7200 原始缓冲（_rawValues 普通 List，无集合通知，上限 MaxChartPoints=7200，溢出批量 RemoveRange），显示集合由 RefreshChart() 用 DownsampleMinMax（min/max 分桶，保首末点+尖峰）降到 ChartWindowPoints=1000 后单次 Replace。
- D3 关动画：_series 构造设 AnimationsSpeed = TimeSpan.Zero。
- D4 固定刷新：OnFrame 仍帧级追加原始缓冲（零重绘），每 500ms 节流触发一次 RefreshChart()；历史预载 LoadPointHistoryAsync 完成立即刷新不等节流。
- D5 不新增全局缓存层：EventBridge 已 200ms 合并（设备频率≠UI 频率），2h 历史由 DB 重载，避免第二份内存副本。

## Alternatives

- 直接把原始缓冲降到 1000 点：丢失 2h 历史与形状细节。
- 引入独立全局缓存层：EventBridge 已合并，重复维护第二份内存副本。

## Rationale

降采样视觉近似、不丢形状，内存与绘制成本大幅下降；IsActive 避免不可见时重绘；动画开销纯浪费；2h 历史由 DB 重载避免重复内存。

## Consequences

- 曲线由「原始 7200 点逐点」变「2h 降采样 ~1000 点显示」；切走/最小化暂停、回来重载；关闭动画。
- 采集/存储/转发链路不动。
