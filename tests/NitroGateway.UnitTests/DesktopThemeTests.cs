using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace NitroGateway.UnitTests;

/// <summary>
/// ADR-037 S1/S2 主题令牌回归：视图 XAML 不得再出现硬编码 # 色值字面量，
/// 令牌集中在 Themes/Styles.xaml；设备状态语义色与 Web 端 DeviceStatusTag.vue 对齐。
/// 从程序集位置向上定位仓库根（含 NitroGateway.slnx）后静态扫描源文件。
/// </summary>
public sealed class DesktopThemeTests
{
    /// <summary>ADR-037 S1/S2 落地后必须存在于 Styles.xaml 的令牌。</summary>
    private static readonly string[] RequiredTokens =
    [
        "AlternatingRowBrush", "BadRowBrush",
        "SeverityEmergencyBrush", "SeverityCriticalBrush", "SeverityWarningBrush",
        "StatusBarBackground", "StatusBarText", "MutedText", "CurveCardBorderBrush",
        "StatusOnlineBrush", "StatusOfflineBrush", "StatusErrorBrush",
        "StatusUnknownBrush", "StatusMaintenanceBrush", "ErrorBrush",
        "BrandGradientBrush", "BoolToVis"
    ];

    [Fact]
    public void View_xaml_files_contain_no_hex_color_literals()
    {
        var viewsDir = Path.Combine(FindRepoRoot(), "src", "NitroGateway.Desktop", "Views");
        Assert.True(Directory.Exists(viewsDir), $"Views 目录不存在: {viewsDir}");

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(viewsDir, "*.xaml"))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.IndexOf('#') >= 0)
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "视图 XAML 出现硬编码 # 色值字面量：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Styles_defines_all_theme_tokens()
    {
        var styles = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "NitroGateway.Desktop", "Themes", "Styles.xaml"));

        foreach (var token in RequiredTokens)
            Assert.Contains($"x:Key=\"{token}\"", styles);
    }

    [Fact]
    public void Device_status_colors_align_with_web_status_tag()
    {
        var styles = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "NitroGateway.Desktop", "Themes", "Styles.xaml"));
        var webCss = File.ReadAllText(Path.Combine(FindRepoRoot(), "web", "src", "components", "DeviceStatusTag.vue"));

        // DeviceStatusTag.vue 的每种状态前景色（color:#xxx）都应已令牌化进 Styles.xaml
        foreach (Match match in Regex.Matches(webCss, @"color:\s*#([0-9a-fA-F]{6})"))
        {
            var hex = match.Groups[1].Value.ToUpperInvariant();
            Assert.Contains($"\"#{hex}\"", styles);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NitroGateway.slnx")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("未找到仓库根（NitroGateway.slnx）");
    }
}
