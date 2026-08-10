# ADR-027: Desktop 模块 code review（2026-08-10）

- 日期: 2026-08-10 | 状态: 已修复（2026-08-10 修复完成） | 来源: review src/NitroGateway.Desktop（ADR-026 实施产物）
- 范围: Desktop 全部 VM/EventBridge/宿主/XAML/测试

## 已修复（条目已清，代码处有注释）
- P1-1 RealtimeViewModel 异步加载竞态 → 版本守卫（_loadVersion，过期回调丢弃）
- P2-1 HistoryViewModel 竞态 + 防重入 → 版本守卫 + `if (IsLoading) return`
- P2-2 AlarmsViewModel 单例捕获 Scoped 仓储 → 注入 IServiceScopeFactory，每次刷新 CreateScope 解析
- P2-3 历史查询无分页 → 上一页/下一页（offset 递增，PageSize=1000，CanGoPrev/CanGoNext）
- P3-1 EventBridge UiFrame.MqttState 注释与实现不符 → 注释改为"最近已知状态"
- P3-2 UiDispatcher.Post 关闭后 BeginInvoke 无保护 → TryBeginInvoke 捕获丢弃（含 .NET 10 取消 Operation 路径）
- P3-3 Serilog WriteTo 索引硬编码 → DesktopPathConfig.FileSinkPathKey 按 Name 匹配 File sink（兼容旧索引 1 环境变量）
- P3-4 全部 VM Dispose 未接线 → MainWindow.Closed → MainViewModel.Dispose 级联子 VM
- P3-5 WinExe 无控制台 Console sink 无效 → appsettings.json 移除 WriteTo[0] Console（File 索引变 0）
- P3-6 HistoryView DatePicker 清空回写失败 → FromDate/ToDate 改 DateTime?，缺失时提示"请选择起止日期"

## 测试（新增 11 个，全量 369 通过）
- RealtimeViewModelTests(2)/HistoryViewModelTests(5)/AlarmsViewModelTests(1)/UiDispatcherTests(1)/DesktopPathConfigTests 扩展(2)

## 遗留测试缺口（非缺陷）
- MainWindow 关闭 drain 流程无自动化测试（依赖真实 host）
- EventBridge 200ms 真实节流无计时测试（现有测试全走手动 Flush）
- LiveCharts2 数据更新（ObservableCollection 追加）未被冒烟覆盖（只测了布局实例化）
