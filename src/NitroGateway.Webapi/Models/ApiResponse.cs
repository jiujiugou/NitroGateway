namespace NitroGateway.Webapi.Models;

public sealed class ApiResponse<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public ApiError? Error { get; init; }
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("O");

    public static ApiResponse<T> Ok(T data) => new() { Success = true, Data = data };
    public static ApiResponse<T> Fail(string code, string message) =>
        new() { Success = false, Error = new ApiError { Code = code, Message = message } };
}

public sealed class ApiError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}
