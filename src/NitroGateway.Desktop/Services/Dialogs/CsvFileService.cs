using System.IO;
using Microsoft.Win32;

namespace NitroGateway.Desktop.Services.Dialogs;

/// <summary>WPF 实现：点位 CSV 打开/保存对话框（Microsoft.Win32，无额外包依赖）。</summary>
public sealed class CsvFileService : ICsvFileService
{
    /// <inheritdoc />
    public string? PickImportCsv()
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入点位 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        return dlg.ShowDialog() == true ? File.ReadAllText(dlg.FileName) : null;
    }

    /// <inheritdoc />
    public bool SaveCsv(string defaultFileName, string content)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出点位 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = defaultFileName
        };
        if (dlg.ShowDialog() != true)
            return false;
        File.WriteAllText(dlg.FileName, content);
        return true;
    }
}
