namespace NitroGateway.Desktop.Services.Dialogs;

public interface ICsvFileService
{
    /// <summary>弹出打开对话框选择 .csv 并读取全文；用户取消返回 null</summary>
    string? PickImportCsv();

    /// <summary>弹出保存对话框写入 CSV 内容；用户取消返回 false</summary>
    bool SaveCsv(string defaultFileName, string content);
}
