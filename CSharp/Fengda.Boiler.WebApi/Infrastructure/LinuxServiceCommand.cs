using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Fengda.Boiler.WebApi.Infrastructure;

/// <summary>
/// Linux systemd 服务管理命令。
/// </summary>
internal static class LinuxServiceCommand
{
    private const string DefaultServiceName = "Fengda.Boiler.WebApi";

    private static readonly HashSet<string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        "install",
        "uninstall",
        "start",
        "stop",
        "restart",
        "status"
    };

    private static readonly Regex ServiceNamePattern = new("^[A-Za-z0-9_.@-]+$", RegexOptions.Compiled);

    public static bool TryHandle(string[] args, IConfiguration configuration)
    {
        if (args.Length == 0 || !Commands.Contains(args[0]))
        {
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("Windows 环境不使用系统服务，请直接运行程序。");
            return true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Console.WriteLine("当前操作系统不支持 systemd 服务管理命令。");
            return true;
        }

        try
        {
            HandleLinuxCommand(command, args.Skip(1).ToArray(), configuration);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"服务命令执行失败：{ex.Message}");
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static void HandleLinuxCommand(string command, string[] serviceRunArgs, IConfiguration configuration)
    {
        var serviceName = ResolveServiceName(configuration);
        var unitName = $"{serviceName}.service";
        var servicePath = $"/etc/systemd/system/{unitName}";

        switch (command)
        {
            case "install":
                Install(serviceName, unitName, servicePath, serviceRunArgs);
                break;
            case "uninstall":
                Uninstall(unitName, servicePath);
                break;
            case "start":
                RunSystemctl(false, "start", unitName);
                break;
            case "stop":
                RunSystemctl(false, "stop", unitName);
                break;
            case "restart":
                RunSystemctl(false, "restart", unitName);
                break;
            case "status":
                RunSystemctl(true, "status", unitName);
                break;
        }
    }

    private static string ResolveServiceName(IConfiguration configuration)
    {
        var serviceName = configuration["ServiceName"];
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = DefaultServiceName;
        }

        if (!ServiceNamePattern.IsMatch(serviceName))
        {
            throw new InvalidOperationException("ServiceName 只能包含字母、数字、点、下划线、横线和 @。");
        }

        return serviceName;
    }

    private static void Install(string serviceName, string unitName, string servicePath, string[] serviceRunArgs)
    {
        if (File.Exists(servicePath))
        {
            throw new InvalidOperationException($"服务 [{unitName}] 已经存在，请先执行 uninstall。");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(servicePath)!);
        File.WriteAllText(servicePath, BuildServiceUnit(serviceName, serviceRunArgs), Encoding.UTF8);

        RunSystemctl(false, "daemon-reload");
        RunSystemctl(false, "enable", unitName);

        Console.WriteLine($"服务 [{unitName}] 已安装。");
        Console.WriteLine($"使用 start 启动服务，使用 status 查看状态。");
    }

    private static void Uninstall(string unitName, string servicePath)
    {
        RunSystemctl(true, "stop", unitName);
        RunSystemctl(true, "disable", unitName);

        if (File.Exists(servicePath))
        {
            File.Delete(servicePath);
        }

        RunSystemctl(false, "daemon-reload");
        RunSystemctl(true, "reset-failed", unitName);

        Console.WriteLine($"服务 [{unitName}] 已卸载。");
    }

    private static string BuildServiceUnit(string serviceName, string[] serviceRunArgs)
    {
        var workingDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var execStart = BuildExecStart(serviceRunArgs);

        return string.Join('\n',
            "[Unit]",
            $"Description={serviceName}",
            "Wants=network-online.target",
            "After=network-online.target",
            "",
            "[Service]",
            "Type=simple",
            $"WorkingDirectory={EscapeSystemdValue(workingDirectory)}",
            $"ExecStart={execStart}",
            "Restart=on-failure",
            "RestartSec=5s",
            "KillSignal=SIGINT",
            $"SyslogIdentifier={serviceName}",
            "Environment=ASPNETCORE_ENVIRONMENT=Production",
            "",
            "[Install]",
            "WantedBy=multi-user.target",
            "");
    }

    private static string BuildExecStart(string[] serviceRunArgs)
    {
        var processPath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;

        var execParts = new List<string> { processPath };
        if (IsDotnetHost(processPath) && !string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            execParts.Add(entryAssemblyPath);
        }

        execParts.AddRange(serviceRunArgs);
        return string.Join(' ', execParts.Select(EscapeSystemdValue));
    }

    private static bool IsDotnetHost(string processPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSystemdValue(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static void RunSystemctl(bool allowFailure, params string[] arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.Write(output);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.Write(error);
        }

        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemctl {string.Join(' ', arguments)} 执行失败，退出码 {process.ExitCode}。");
        }
    }
}
