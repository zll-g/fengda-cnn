using System.Text.Json;
using Fengda.Boiler.WebApi.Models;

namespace Fengda.Boiler.WebApi.Infrastructure;

/// <summary>
/// 将未处理异常转换为统一中文 JSON 响应。
/// </summary>
public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate next;
    private readonly ILogger<ApiExceptionMiddleware> logger;

    /// <summary>
    /// 创建统一异常处理中间件。
    /// </summary>
    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        this.next = next;
        this.logger = logger;
    }

    /// <summary>
    /// 执行请求并捕获异常。
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (BusinessException ex)
        {
            await WriteResponseAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (BadHttpRequestException)
        {
            await WriteResponseAsync(context, 400, "请求数据格式不正确，请检查请求内容。").ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteResponseAsync(context, 400, "JSON 数据格式不正确，请检查字段名称、类型和结构。").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 WebApi 请求时发生未处理异常。");
            await WriteResponseAsync(context, 500, "服务器内部发生异常，请查看运行日志。").ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(ApiResponses.Failure<object?>(code, message)).ConfigureAwait(false);
    }
}
