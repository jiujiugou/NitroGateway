# ADR-041: 表头与数据行水平对齐（DataGridCell Padding 不生效修复）

- 日期: 2026-08-13 | 状态: 已实施 | 来源: 用户「这个悬浮问题解决了，但是和上面没有对齐」（设备表格表头 vs 数据行文字左缘）
- 范围: src/NitroGateway.Desktop/Themes/Styles.xaml（共享 DataGridStyle.CellStyle）
- 三问: 为什么做=表头文字（Padding=14 左缩进 14 DIP≈17px @125%DPI），而数据行所有列内容贴住单元格左边界（0px），横向错位 17px 视觉突兀；验收=从站/上次采集/点位数/错误及模板列（图标/徽章）文字左缘与表头一致，build+单测全绿；不做=多列表格行内文字与表头错位
- G1 确认: 纯 XAML 样式/视觉变更，无接口/数据/行为变更，直接实施
- 根因: WPF 默认 DataGridCell 模板不应用 Padding——CellStyle 已设 Padding="14,0" 但从未生效；DataGridTextColumn 生成的 TextBlock 与模板列根元素全部从单元格左缘 0px 起排，而 ColumnHeaderStyle Padding=14 生效，表头文字在 14 DIP 处，故水平差 17px（UIAutomation 实测：header x=1040/1115/1235/1305 vs 行文字 x=1023/1098/1218/1288）
- 修复: DataGridStyle.CellStyle 自定义 ControlTemplate——Border Padding="{TemplateBinding Padding}" 包裹 ContentPresenter（保留 Background/BorderThickness 绑定 + IsSelected 触发器），使所有列内容（含 TextBlock）统一按单元格 14 DIP 内边距排布；与表头对齐
- 坑: 自定义模板需保留 VerticalContentAlignment 绑定（否则破坏 ADR-040 垂直居中）；TemplateBinding Background 保证选中/悬停行背景仍生效
- 验证: dotnet build 0 错误；单测 559 全绿；UIAutomation 重启后逐列比对——从站 1040=1040、上次采集 1115=1115、点位数 1235=1235、错误 1305=1305（修复前差 17px），图标/徽章左缘=表头 525/930；像素扫描 tools/screen-aligned.png 二次确认（行文字 x=894/969/1088/1158 ≈ 表头 893/968/1088/1158）
- 未提交: git 提交由用户执行
