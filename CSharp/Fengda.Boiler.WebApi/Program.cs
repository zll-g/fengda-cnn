using System.Reflection;
using System.Text.Json;
using Fengda.Boiler.Monitoring;
using Fengda.Boiler.WebApi.Infrastructure;
using Fengda.Boiler.WebApi.Models;
using Fengda.Boiler.WebApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
if (LinuxServiceCommand.TryHandle(args, builder.Configuration))
{
    return;
}

var configuredMonitorOptions = builder.Configuration
    .GetSection("Monitoring")
    .Get<BoilerMonitorOptions>() ?? new BoilerMonitorOptions();
var monitorOutputDirectory = Path.IsPathRooted(configuredMonitorOptions.OutputDirectory)
    ? configuredMonitorOptions.OutputDirectory
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredMonitorOptions.OutputDirectory));
var monitorDatabaseDirectory = Path.IsPathRooted(configuredMonitorOptions.DatabaseDirectory)
    ? configuredMonitorOptions.DatabaseDirectory
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredMonitorOptions.DatabaseDirectory));
var monitorFaultDatabasePath = Path.IsPathRooted(configuredMonitorOptions.FaultDatabasePath)
    ? configuredMonitorOptions.FaultDatabasePath
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredMonitorOptions.FaultDatabasePath));
var monitorOptions = configuredMonitorOptions with
{
    OutputDirectory = monitorOutputDirectory,
    DatabaseDirectory = monitorDatabaseDirectory,
    FaultDatabasePath = monitorFaultDatabasePath
};
var predictionOptions = builder.Configuration
    .GetSection("Prediction")
    .Get<PredictionOptions>() ?? new PredictionOptions();
var configuredEarlyWarningOptions = builder.Configuration
    .GetSection("EarlyWarning")
    .Get<EarlyWarningOptions>() ?? new EarlyWarningOptions();
var earlyWarningOptions = configuredEarlyWarningOptions;
var configuredEmissionBoundsOptions = builder.Configuration
    .GetSection("EmissionBounds")
    .Get<EmissionBoundsOptions>() ?? new EmissionBoundsOptions();
var emissionBoundsDatabasePath = Path.IsPathRooted(configuredEmissionBoundsOptions.DatabasePath)
    ? configuredEmissionBoundsOptions.DatabasePath
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configuredEmissionBoundsOptions.DatabasePath));
var emissionBoundsOptions = configuredEmissionBoundsOptions with { DatabasePath = emissionBoundsDatabasePath };

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new ChinaDateTimeJsonConverter());
    });
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        return new OkObjectResult(ApiResponses.Failure<object?>(
            400,
            "请求参数格式不正确，请检查时间格式、参数名称和参数类型。"));
    };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "丰达启动锅炉实时预测 WebApi",
        Version = "v1",
        Description = "提供锅炉 OPC UA 历史数据查询、实时预测、历史预测、FGR 建议和 NOx/O₂ 排放统计边界监测接口。所有业务结果均使用 code/message/data 统一结构返回。"
    });
    options.CustomSchemaIds(type => type.FullName?.Replace("+", ".") ?? type.Name);

    foreach (var xmlFile in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.xml"))
    {
        options.IncludeXmlComments(xmlFile, includeControllerXmlComments: true);
    }
});

builder.Services.AddSingleton(monitorOptions);
builder.Services.AddSingleton(predictionOptions);
builder.Services.AddSingleton(earlyWarningOptions);
builder.Services.AddSingleton(emissionBoundsOptions);
builder.Services.AddSingleton<AspNetMonitorLogger>();
builder.Services.AddSingleton<BoilerSnapshotCache>();
builder.Services.AddSingleton(serviceProvider =>
    new BoilerSnapshotStore(
        monitorOptions.DatabaseDirectory,
        serviceProvider.GetRequiredService<AspNetMonitorLogger>(),
        monitorOptions.LegacyDatabasePath));
builder.Services.AddSingleton(serviceProvider =>
    new RunningFaultStore(
        monitorOptions.FaultDatabasePath,
        serviceProvider.GetRequiredService<AspNetMonitorLogger>()));
builder.Services.AddSingleton(serviceProvider =>
    new EarlyWarningStore(
        monitorOptions.FaultDatabasePath,
        serviceProvider.GetRequiredService<AspNetMonitorLogger>()));
builder.Services.AddSingleton<EarlyWarningRuntimeCache>();
builder.Services.AddSingleton<EmissionBoundsRuntimeCache>();
builder.Services.AddSingleton(serviceProvider =>
    new EmissionBoundsStore(emissionBoundsOptions.DatabasePath));
builder.Services.AddSingleton<EarlyWarningHistoricalAnalysisService>();
builder.Services.AddSingleton<PredictionApplicationService>();
builder.Services.AddHostedService<BoilerMonitoringHostedService>();
builder.Services.AddHostedService<EarlyWarningHostedService>();
builder.Services.AddHostedService<EmissionBoundsHostedService>();
builder.Services.AddHostedService<MinuteForecastCacheHostedService>();

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseStatusCodePages(async statusCodeContext =>
{
    if (!statusCodeContext.HttpContext.Request.Path.StartsWithSegments("/api"))
    {
        return;
    }

    var response = statusCodeContext.HttpContext.Response;
    var originalStatusCode = response.StatusCode;
    response.StatusCode = StatusCodes.Status200OK;
    response.ContentType = "application/json; charset=utf-8";
    var message = originalStatusCode switch
    {
        StatusCodes.Status404NotFound => "请求的接口不存在。",
        StatusCodes.Status405MethodNotAllowed => "请求使用了不支持的 HTTP 方法。",
        _ => $"请求处理失败，HTTP 状态码为 {originalStatusCode}。"
    };
    await response.WriteAsJsonAsync(ApiResponses.Failure<object?>(originalStatusCode, message)).ConfigureAwait(false);
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "丰达启动锅炉实时预测 WebApi v1");
    options.DocumentTitle = "丰达启动锅炉实时预测 WebApi";
});
app.MapControllers();
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(ApiResponses.Failure<object?>(404, "请求的接口不存在。"))
            .ConfigureAwait(false);
        return;
    }

    if (Path.HasExtension(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var webRoot = app.Environment.WebRootPath
        ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    var indexPath = Path.Combine(webRoot, "index.html");
    if (!File.Exists(indexPath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("静态站尚未构建。").ConfigureAwait(false);
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath).ConfigureAwait(false);
});

app.Run();

/// <summary>
/// WebApi 程序入口类型，供集成测试使用。
/// </summary>
public partial class Program;
