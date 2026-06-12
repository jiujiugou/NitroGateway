namespace NitroGateway.Shared;

/// <summary>
/// 无返回值的操作结果。成功时 <see cref="IsSuccess"/> 为 true，失败时携带 <see cref="Error"/>。
/// </summary>
public sealed class OperationResult
{
    /// <summary>是否成功</summary>
    public bool IsSuccess => Error is null;

    /// <summary>是否失败</summary>
    public bool IsFailure => Error is not null;

    /// <summary>失败时的错误信息，成功时为 null</summary>
    public OperationalError? Error { get; }

    private OperationResult() { }

    private OperationResult(OperationalError error)
    {
        Error = error;
    }

    /// <summary>创建一个成功结果</summary>
    public static OperationResult Success() => new();

    /// <summary>创建一个失败结果</summary>
    public static OperationResult Failure(OperationalError error) => new(error);

    /// <summary>隐式转换：从 <see cref="OperationalError"/> 转为失败结果</summary>
    public static implicit operator OperationResult(OperationalError error) => Failure(error);
}

/// <summary>
/// 带返回值的操作结果。成功时通过 <see cref="Value"/> 获取数据，失败时携带 <see cref="Error"/>。
/// </summary>
public sealed class OperationResult<T>
{
    /// <summary>成功时的返回值</summary>
    public T? Value { get; }

    /// <summary>是否成功</summary>
    public bool IsSuccess => Error is null;

    /// <summary>是否失败</summary>
    public bool IsFailure => Error is not null;

    /// <summary>失败时的错误信息</summary>
    public OperationalError? Error { get; }

    private OperationResult(T value)
    {
        Value = value;
    }

    private OperationResult(OperationalError error)
    {
        Error = error;
    }

    /// <summary>创建一个成功结果</summary>
    public static OperationResult<T> Success(T value) => new(value);

    /// <summary>创建一个失败结果</summary>
    public static OperationResult<T> Failure(OperationalError error) => new(error);

    /// <summary>隐式转换：从值转为成功结果</summary>
    public static implicit operator OperationResult<T>(T value) => Success(value);

    /// <summary>隐式转换：从 <see cref="OperationalError"/> 转为失败结果</summary>
    public static implicit operator OperationResult<T>(OperationalError error) => Failure(error);
}
