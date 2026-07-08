using Microsoft.Data.Sqlite;
using NitroGateway.Shared;

namespace NitroGateway.Persistence.Sqlite;

/// <summary>SQLite 异常 → OperationalError 映射</summary>
internal static class SqliteErrorClassifier
{
    /// <summary>
    /// 根据 SqliteException 的 ErrorCode 返回对应的 OperationalError。
    /// 非 SqliteException 返回 General。
    /// </summary>
    public static OperationalError Classify(Exception ex, string context)
    {
        if (ex is not SqliteException sqlEx)
            return OperationalError.Storage($"{context}: {ex.Message}");

        return sqlEx.SqliteErrorCode switch
        {
            13 => OperationalError.StorageFull($"{context} (磁盘满): {sqlEx.Message}"),       // SQLITE_FULL
            10 => OperationalError.StorageFull($"{context} (I/O 错误): {sqlEx.Message}"),      // SQLITE_IOERR
            11 => OperationalError.StorageFull($"{context} (数据库损坏): {sqlEx.Message}"),    // SQLITE_CORRUPT
            5  => OperationalError.DatabaseLocked($"{context} (数据库锁定): {sqlEx.Message}"), // SQLITE_BUSY
            _  => OperationalError.Storage($"{context}: {sqlEx.Message}")
        };
    }
}
