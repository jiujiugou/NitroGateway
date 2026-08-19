namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>
/// 点位 CSV 文件选择/保存抽象（导入导出用，ADR-029 同款对话框模式）。
/// ViewModel 依赖本接口而非 Microsoft.Win32 对话框，便于单测用 fake 替身；
/// WPF 实现见 <see cref="CsvFileService"/>。
/// </summary>
public interface ICsvFileService
{
    /// <summary>弹出打开对话框选择 .csv 并读取全文；用户取消返回 null</summary>
    string? PickImportCsv();

    /// <summary>弹出保存对话框写入 CSV 内容；用户取消返回 false</summary>
    bool SaveCsv(string defaultFileName, string content);
}
