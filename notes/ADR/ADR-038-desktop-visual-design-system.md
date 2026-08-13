# ADR-038: 桌面视觉设计系统重写（美观化）

- 日期: 2026-08-13 | 状态: 已实施 | 来源: 用户「为什么你写的桌面ui这么丑陋呢，优化成很美观，符合人类审美的那种」（桌面 WPF 端，非 Web）
- 范围: src/NitroGateway.Desktop 的 Themes/Styles.xaml + Views/*.xaml + ViewModels/RealtimeViewModel.cs（曲线色）
- 三问: 为什么做=桌面端是现场主操作面，功能已全但视觉停留在默认控件观感；验收=统一设计系统（主色/圆角/投影/深色侧栏）且 build+558 单测全绿；不做=主操作面观感平庸，与「美观」诉求不符
- 设计系统: 靛蓝主色 #2563EB；深色侧栏（#0F172A 渐变 + Segoe MDL2 导航图标 + 选中靛蓝胶囊）；卡片化（圆角 8 + 1px 边框 + 轻投影）；页头 + 卡片工具栏 + 表单化弹窗；ADR-037 S2 状态语义色/RequiredTokens 全部保留
- 坑 1（代码位置: MainWindow 等 5 个窗口）: WPF 隐式 Style TargetType="Window" 对 Window 子类不生效（探针证实子类 Style=null、背景纯白）→ 各窗口显式声明 Background/Foreground/FontFamily/FontSize
- 坑 2（同 5 窗口）: Window 元素自身属性上的 StaticResource 在其 Resources 就绪前解析，无 Application 的冒烟测试抛「找不到 PageBackground」→ 元素属性级改 DynamicResource（运行时从 Application.Resources 解析，测试不抛），内容区仍用 StaticResource
- 验证: dotnet build NitroGateway.slnx 0 错误；单测 558 全绿（含 DesktopThemeTests/DesktopViewSmokeTests）；capture-window-dpi.ps1 物理分辨率（1550×950，系统 DPI 125%）像素采样确认侧栏深色/导航选中靛蓝/画布 #F3F5F9/卡片白/状态栏 #0F172A 均正确
- 未提交: git 提交由用户执行
