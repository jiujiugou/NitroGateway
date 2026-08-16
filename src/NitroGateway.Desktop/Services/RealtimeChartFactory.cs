using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace NitroGateway.Desktop.Services;

/// <summary>
/// 实时曲线图表配置工厂（ADR-045 P3 表现层关注点）。
/// 把「图表怎么画」从 RealtimeViewModel 中剥离：
/// 配色/坐标轴/labeler 等渲染细节集中在此，ViewModel 只负责数据与绑定。
/// </summary>
public static class RealtimeChartFactory
{
    /// <summary>
    /// 创建实时曲线系列：实线 2px 靛蓝 + 同色系淡蓝渐变填充。
    /// 颜色与 Styles.xaml PrimaryBrush #2563EB 对齐；关动画避免每帧产生逐点动画对象（ADR-045 P3）。
    /// </summary>
    public static LineSeries<DateTimePoint> CreateSeries() => new()
    {
        Name = "实时值",
        Fill = new LinearGradientPaint(new SKColor(37, 99, 235, 60), new SKColor(37, 99, 235, 0)),
        GeometrySize = 0,
        LineSmoothness = 0.2,
        Stroke = new SolidColorPaint(SKColor.Parse("#2563EB")) { StrokeThickness = 2 },
        // ADR-045 P3：关动画，避免每帧产生逐点动画对象
        AnimationsSpeed = TimeSpan.Zero
    };

    /// <summary>
    /// 创建 X（时间）/Y 坐标轴（浅灰文字与分隔线，与主题一致）。
    /// 空曲线时 LiveCharts 会用 NaN 等占位值调 labeler，(long)NaN 为负数
    /// 会令 new DateTime 抛 Ticks 越界，故 labeler 先做范围保护。
    /// </summary>
    public static RealtimeAxes CreateAxes() => new(
        new Axis
        {
            Labeler = value =>
            {
                var ticks = (long)value;
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                    return string.Empty;
                return new DateTime(ticks).ToString("HH:mm:ss");
            },
            TextSize = 11,
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#64748B")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#E2E8F0")) { StrokeThickness = 1 }
        },
        new Axis
        {
            TextSize = 11,
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#64748B")),
            SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#E2E8F0")) { StrokeThickness = 1 }
        });
}

/// <summary>实时曲线 X/Y 坐标轴对（避免数组下标约定带来的阅读负担）。</summary>
public sealed record RealtimeAxes(Axis X, Axis Y);
