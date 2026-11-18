namespace Fengda.Boiler.WebApi.Models;

/// <summary>
/// WebApi 统一响应结构。
/// </summary>
/// <typeparam name="T">响应数据类型。</typeparam>
public sealed class ApiResponse<T>
{
    /// <summary>
    /// 业务状态码，成功时为 200。
    /// </summary>
    public int Code { get; init; }

    /// <summary>
    /// 中文响应消息，成功时为空字符串。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// 业务响应数据。
    /// </summary>
    public T? Data { get; init; }
}

/// <summary>
/// 统一响应创建工具。
/// </summary>
public static class ApiResponses
{
    /// <summary>
    /// 创建成功响应。
    /// </summary>
    public static ApiResponse<T> Success<T>(T data)
    {
        return new ApiResponse<T> { Code = 200, Message = string.Empty, Data = data };
    }

    /// <summary>
    /// 创建失败响应。
    /// </summary>
    public static ApiResponse<T> Failure<T>(int code, string message)
    {
        return new ApiResponse<T> { Code = code, Message = message, Data = default };
    }
}

/// <summary>
/// 可通过统一响应返回的业务异常。
/// </summary>
public sealed class BusinessException : Exception
{
    /// <summary>
    /// 创建业务异常。
    /// </summary>
    public BusinessException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// 业务状态码。
    /// </summary>
    public int Code { get; }
}
