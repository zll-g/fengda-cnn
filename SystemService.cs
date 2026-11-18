using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace Fengda.Boiler.WebApi
{
    class SystemService
    {
        #region 服务信息

        public static string ServiceName { get; private set; } = "Fengda.Boiler.WebApi";

        #endregion

        #region 服务运行

        static HashSet<string> FirstArgs = new HashSet<string> { "", "install", "uninstall", "start", "stop", "-displayname", "restart", "status" };

        public static void Run(string[] args)
        {
            var configuration = (ConfigurationRoot)new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json").Build();
            var serviceName = configuration["ServiceName"];
            if (!string.IsNullOrWhiteSpace(serviceName))
                ServiceName = serviceName;

            var firstArg = args.Length == 0 ? "" : args[0].Trim().ToLower();
            if (!FirstArgs.Contains(firstArg))
            {
                Log.Info($"未能识别的参数,\"{firstArg}\",无法启动!");
                return;
            }
            if (firstArg == "")
            {
                Mutex mutex = new Mutex(true, $"Global\\{ServiceName}", out bool createdNew);
                if (!createdNew)
                {
                    Console.WriteLine($"服务已启动,无法再次启动,3秒后自动退出!");
                    Thread.Sleep(3000);
                    return;
                }
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
				RunService();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                switch (firstArg)
                {
                    case "install":
                        {
                            string servicePath = $"/usr/lib/systemd/system/{ServiceName}.service";
                            if (File.Exists(servicePath))
                            {
                                Console.WriteLine($"服务[{ServiceName}]已经存在,无法覆盖安装,请先卸载原服务!");
                                return;
                            }
                            var workingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                            var execStart = Process.GetCurrentProcess().MainModule!.FileName;
                            var serviceCode = $"[Unit]\nDescription={ServiceName}\n[Install]\nWantedBy=multi-user.target\n[Service]\nType=simple\nWorkingDirectory={workingDirectory}\nExecStart={execStart}\nPrivateTmp=true\nRestart=on-abort\nRestartsec=5s";
                            File.WriteAllBytes(servicePath, Encoding.UTF8.GetBytes(serviceCode));
                            $"systemctl enable {ServiceName}.service".Bash();
                            $"systemctl daemon-reload".Bash();
                        }
                        break;
                    case "uninstall":
                        {
                            $"systemctl stop {ServiceName}.service".Bash();
                            $"systemctl disable {ServiceName}.service".Bash();
                            File.Delete($"/usr/lib/systemd/system/{ServiceName}.service");
                            $"systemctl daemon-reload".Bash();
                        }
                        break;
                    case "start": $"systemctl start {ServiceName}.service".Bash(); break;
                    case "stop": $"systemctl stop {ServiceName}.service".Bash(); break;
                    case "restart": $"systemctl restart {ServiceName}.service".Bash(); break;
                    case "status": Console.WriteLine($"systemctl status {ServiceName}.service".BashResult()); break;
                    default:
                        {
                            //linux下开机120秒内延迟10秒启动
                            if ((DateTimeEx.Now - new DateTime()).TotalSeconds < 120)
                                Thread.Sleep(10 * 1000);
                            Console.Write("服务启动");
                            Task.Run(() =>
                            {
                                "echo 99999999 > /proc/sys/fs/inotify/max_user_watches".Bash();
                                "echo 9999 > /proc/sys/fs/inotify/max_user_instances".Bash();
                            });

							RunService();

                            break;
                        }
                }
            }
        }

        //正式服务
		private static void RunService()
		{
			//在此启动正式服务			
			
		}
		
		#endregion


    }
	
	public static class BashExtensions
    {

        public static void Bash(this string cmd)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var escapedArgs = cmd.Replace("\"", "\"\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{escapedArgs}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                process.WaitForExit();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var escapedArgs = cmd.Replace("\"", "\\\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{escapedArgs}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                process.WaitForExit();
            }
        }

        public static string BashResult(this string cmd)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var escapedArgs = cmd.Replace("\"", "\"\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{escapedArgs}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                string result = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return result;
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var escapedArgs = cmd.Replace("\"", "\\\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{escapedArgs}\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
                string result = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return result;
            }
            return "";
        }

        internal static void BashAsync(this string cmd)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var escapedArgs = cmd.Replace("\"", "\"\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c \"{escapedArgs}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var escapedArgs = cmd.Replace("\"", "\\\"");
                var process = new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{escapedArgs}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                process.Start();
            }
        }

    }
}
