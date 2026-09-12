using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("iQing 校园网自动登录")]
[assembly: AssemblyDescription("在 Windows 登录前自动连接并认证 iQing 校园网")]
[assembly: AssemblyCompany("Campus Auto Login")]
[assembly: AssemblyProduct("iQing 校园网自动登录")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CampusAutoLogin
{
    internal static class AppPaths
    {
        internal const string ServiceName = "CampusAutoLoginService";
        internal const string WifiName = "iQing";
        internal const string InterfaceName = "WLAN";
        internal static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CampusAutoLogin");
        internal static readonly string InstalledExe = Path.Combine(InstallDirectory, "CampusAutoLogin.exe");
        internal static readonly string CredentialFile = Path.Combine(InstallDirectory, "credential.bin");
        internal static readonly string LogFile = Path.Combine(InstallDirectory, "service.log");
    }

    internal sealed class StoredCredential
    {
        internal string UserName;
        internal string Password;
    }

    internal static class CredentialStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("iQing-CampusAutoLogin-v1");

        internal static void Save(string userName, string password)
        {
            AppInstaller.EnsureSecureDirectory();
            byte[] clear;
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(1);
                writer.Write(userName ?? String.Empty);
                writer.Write(password ?? String.Empty);
                writer.Flush();
                clear = stream.ToArray();
            }

            try
            {
                byte[] encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine);
                File.WriteAllBytes(AppPaths.CredentialFile, encrypted);
                Array.Clear(encrypted, 0, encrypted.Length);
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
            }
        }

        internal static StoredCredential Load()
        {
            if (!File.Exists(AppPaths.CredentialFile)) return null;
            byte[] encrypted = File.ReadAllBytes(AppPaths.CredentialFile);
            byte[] clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                using (MemoryStream stream = new MemoryStream(clear))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    int version = reader.ReadInt32();
                    if (version != 1) throw new InvalidDataException("不支持的凭据版本。");
                    return new StoredCredential { UserName = reader.ReadString(), Password = reader.ReadString() };
                }
            }
            finally
            {
                Array.Clear(clear, 0, clear.Length);
                Array.Clear(encrypted, 0, encrypted.Length);
            }
        }
    }

    internal static class SafeLog
    {
        private static readonly object SyncRoot = new object();

        internal static void Write(string message)
        {
            try
            {
                AppInstaller.EnsureSecureDirectory();
                string safe = Regex.Replace(message ?? String.Empty, "[\\r\\n]+", " ");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + safe + Environment.NewLine;
                lock (SyncRoot)
                {
                    File.AppendAllText(AppPaths.LogFile, line, new UTF8Encoding(false));
                    FileInfo info = new FileInfo(AppPaths.LogFile);
                    if (info.Length > 512 * 1024)
                    {
                        string oldFile = AppPaths.LogFile + ".old";
                        if (File.Exists(oldFile)) File.Delete(oldFile);
                        File.Move(AppPaths.LogFile, oldFile);
                    }
                }
            }
            catch { }
        }

        internal static string Tail(int count)
        {
            try
            {
                if (!File.Exists(AppPaths.LogFile)) return "暂无运行日志。";
                string[] lines = File.ReadAllLines(AppPaths.LogFile, Encoding.UTF8);
                return String.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - count)).ToArray());
            }
            catch (Exception ex)
            {
                return "读取日志失败：" + ex.Message;
            }
        }
    }

    internal sealed class CommandResult
    {
        internal int ExitCode;
        internal string Output;
        internal string Error;
    }

    internal static class CommandRunner
    {
        internal static CommandResult Run(string fileName, IEnumerable<string> arguments, string standardInput, int timeoutMs)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = fileName;
            start.Arguments = BuildArguments(arguments);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.RedirectStandardInput = standardInput != null;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;

            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.Start();
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                if (standardInput != null)
                {
                    process.StandardInput.Write(standardInput);
                    process.StandardInput.Close();
                }
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new System.TimeoutException(Path.GetFileName(fileName) + " 执行超时。");
                }
                Task.WaitAll(new Task[] { outputTask, errorTask }, 2000);
                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = outputTask.IsCompleted ? outputTask.Result : String.Empty,
                    Error = errorTask.IsCompleted ? errorTask.Result : String.Empty
                };
            }
        }

        private static string BuildArguments(IEnumerable<string> arguments)
        {
            return String.Join(" ", arguments.Select(QuoteArgument).ToArray());
        }

        private static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length > 0 && !Regex.IsMatch(value, "[\\s\"]")) return value;
            StringBuilder result = new StringBuilder("\"");
            int slashCount = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    slashCount++;
                }
                else if (c == '"')
                {
                    result.Append('\\', slashCount * 2 + 1);
                    result.Append('"');
                    slashCount = 0;
                }
                else
                {
                    result.Append('\\', slashCount);
                    result.Append(c);
                    slashCount = 0;
                }
            }
            result.Append('\\', slashCount * 2);
            result.Append('"');
            return result.ToString();
        }
    }

    internal static class WifiManager
    {
        internal static string GetCurrentSsid()
        {
            CommandResult result = CommandRunner.Run("netsh.exe", new[] { "wlan", "show", "interfaces" }, null, 8000);
            Match match = Regex.Match(result.Output, @"(?m)^\s*SSID\s*:\s*(.+?)\s*$");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        internal static string GetWlanIpv4()
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!String.Equals(network.Name, AppPaths.InterfaceName, StringComparison.OrdinalIgnoreCase)) continue;
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = address.Address.ToString();
                    if (!ip.StartsWith("169.254.") && ip != "127.0.0.1") return ip;
                }
            }
            return null;
        }

        internal static void EnsureIqingProfile()
        {
            string xml = "<?xml version=\"1.0\"?>" +
                "<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">" +
                "<name>iQing</name><SSIDConfig><SSID><name>iQing</name></SSID><nonBroadcast>false</nonBroadcast></SSIDConfig>" +
                "<connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                "<MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption>" +
                "<useOneX>false</useOneX></authEncryption></security></MSM></WLANProfile>";
            string temp = Path.Combine(Path.GetTempPath(), "iQing-profile-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(temp, xml, new UTF8Encoding(false));
                CommandResult result = CommandRunner.Run("netsh.exe", new[] { "wlan", "add", "profile", "filename=" + temp, "user=all" }, null, 10000);
                if (result.ExitCode != 0) throw new InvalidOperationException("创建全用户 iQing 配置失败：" + result.Error.Trim());
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        internal static void TryConnectIqing()
        {
            try
            {
                CommandRunner.Run("netsh.exe", new[] { "wlan", "connect", "name=iQing", "ssid=iQing", "interface=WLAN" }, null, 10000);
            }
            catch { }
        }
    }

    internal sealed class LoginResult
    {
        internal bool Success;
        internal string Message;
    }

    internal static class PortalClient
    {
        private const string PortalHost = "10.1.7.3";
        private const string StatusUrl = "http://10.1.7.3/eportal/redirectortosuccess.jsp";
        private const string LoginUrl = "http://10.1.7.3/eportal/InterFace.do?method=login";

        internal static bool IsOnline(string localAddress)
        {
            try
            {
                string headers = Curl(localAddress, new[] {
                    "--connect-timeout", "4", "--max-time", "8", "--output", "NUL", "--dump-header", "-", StatusUrl
                }, null, 12000);
                return Regex.IsMatch(headers, @"(?im)^Location:\s*https?://[^\r\n]+/eportal/(?:\./)?success\.jsp(?:\?|\s|$)");
            }
            catch { return false; }
        }

        internal static LoginResult Login(string localAddress, StoredCredential credential)
        {
            string portalUrl = FindPortalUrl(localAddress);
            if (portalUrl == null) return new LoginResult { Success = false, Message = "未取得本次认证所需的动态参数。" };

            Uri uri = new Uri(portalUrl);
            string query = uri.Query.TrimStart('?');
            if (query.Length == 0) return new LoginResult { Success = false, Message = "认证入口缺少动态 queryString。" };

            string body = "userId=" + DoubleEncode(credential.UserName) +
                "&password=" + DoubleEncode(credential.Password) +
                "&service=&queryString=" + DoubleEncode(query) +
                "&operatorPwd=&operatorUserId=&validcode=&passwordEncrypt=false";

            string response = Curl(localAddress, new[] {
                "--connect-timeout", "5", "--max-time", "15",
                "--header", "Content-Type: application/x-www-form-urlencoded; charset=UTF-8",
                "--header", "Referer: " + portalUrl,
                "--data-binary", "@-", LoginUrl
            }, body, 20000).Trim();

            Dictionary<string, object> data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(response);
            object resultValue;
            object messageValue;
            string result = data.TryGetValue("result", out resultValue) ? Convert.ToString(resultValue) : String.Empty;
            string message = data.TryGetValue("message", out messageValue) ? Convert.ToString(messageValue) : String.Empty;
            return new LoginResult { Success = String.Equals(result, "success", StringComparison.OrdinalIgnoreCase), Message = message };
        }

        private static string FindPortalUrl(string localAddress)
        {
            string[][] targets = new[] {
                new[] { StatusUrl, String.Empty },
                new[] { "http://1.1.1.1/redirect", "www.msftconnecttest.com" },
                new[] { "http://223.5.5.5/", String.Empty },
                new[] { "http://123.123.123.123/", String.Empty }
            };

            foreach (string[] target in targets)
            {
                try
                {
                    List<string> args = new List<string> {
                        "--connect-timeout", "3", "--max-time", "6", "--output", "NUL", "--dump-header", "-"
                    };
                    if (target[1].Length > 0) { args.Add("--header"); args.Add("Host: " + target[1]); }
                    args.Add(target[0]);
                    string headers = Curl(localAddress, args.ToArray(), null, 10000);
                    Match locationMatch = Regex.Match(headers, @"(?im)^Location:\s*([^\r\n]+)");
                    if (!locationMatch.Success) continue;
                    string location = System.Net.WebUtility.HtmlDecode(locationMatch.Groups[1].Value.Trim());
                    Uri resolved = new Uri(new Uri(target[0]), location);
                    if (String.Equals(resolved.Host, PortalHost, StringComparison.OrdinalIgnoreCase) &&
                        resolved.AbsolutePath.IndexOf("/eportal/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        resolved.Query.Length > 1 &&
                        !resolved.AbsolutePath.EndsWith("success.jsp", StringComparison.OrdinalIgnoreCase))
                    {
                        return resolved.AbsoluteUri;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string Curl(string localAddress, string[] arguments, string input, int timeoutMs)
        {
            List<string> args = new List<string> { "--noproxy", "*", "--interface", localAddress, "--silent", "--show-error" };
            args.AddRange(arguments);
            CommandResult result = CommandRunner.Run("curl.exe", args, input, timeoutMs);
            if (result.ExitCode != 0)
            {
                string error = Regex.Replace(result.Error ?? String.Empty, "[\\r\\n]+", " ").Trim();
                throw new InvalidOperationException(error.Length > 0 ? error : "curl 返回 " + result.ExitCode);
            }
            return result.Output ?? String.Empty;
        }

        private static string DoubleEncode(string value)
        {
            return Uri.EscapeDataString(Uri.EscapeDataString(value ?? String.Empty));
        }
    }

    internal sealed class LoginEngine
    {
        internal void Run(WaitHandle stopSignal)
        {
            int failures = 0;
            SafeLog.Write("后台服务已启动，等待 iQing Wi-Fi。");
            while (!stopSignal.WaitOne(0))
            {
                int delaySeconds = 3;
                try
                {
                    string ssid = WifiManager.GetCurrentSsid();
                    if (String.IsNullOrEmpty(ssid))
                    {
                        WifiManager.TryConnectIqing();
                        delaySeconds = 2;
                    }
                    else if (!String.Equals(ssid, AppPaths.WifiName, StringComparison.Ordinal))
                    {
                        failures = 0;
                        delaySeconds = 5;
                    }
                    else
                    {
                        string ip = WifiManager.GetWlanIpv4();
                        if (String.IsNullOrEmpty(ip))
                        {
                            delaySeconds = 2;
                        }
                        else if (PortalClient.IsOnline(ip))
                        {
                            failures = 0;
                            delaySeconds = 30;
                        }
                        else
                        {
                            StoredCredential credential = CredentialStore.Load();
                            if (credential == null) throw new InvalidOperationException("尚未保存账号密码。");
                            SafeLog.Write("检测到 iQing 未认证，正在自动登录。");
                            LoginResult result = PortalClient.Login(ip, credential);
                            credential.Password = null;
                            if (result.Success)
                            {
                                if (stopSignal.WaitOne(3000)) break;
                                if (PortalClient.IsOnline(ip))
                                {
                                    SafeLog.Write("自动登录成功，联网验证通过。");
                                    failures = 0;
                                    delaySeconds = 30;
                                }
                                else
                                {
                                    SafeLog.Write("门户返回成功，但联网验证尚未通过。");
                                    failures++;
                                    delaySeconds = 8;
                                }
                            }
                            else
                            {
                                string message = Regex.Replace(result.Message ?? String.Empty, "[\\r\\n]+", " ");
                                SafeLog.Write("自动登录未成功：" + message);
                                failures++;
                                delaySeconds = 8;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeLog.Write("本轮检查失败：" + ex.Message);
                    failures++;
                    delaySeconds = failures >= 3 ? 30 : 8;
                }

                if (failures >= 3) failures = 0;
                if (stopSignal.WaitOne(delaySeconds * 1000)) break;
            }
            SafeLog.Write("后台服务已停止。");
        }
    }

    internal sealed class CampusLoginService : ServiceBase
    {
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);
        private Thread worker;

        internal CampusLoginService()
        {
            ServiceName = AppPaths.ServiceName;
            CanStop = true;
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            stopEvent.Reset();
            worker = new Thread(delegate() { new LoginEngine().Run(stopEvent); });
            worker.IsBackground = true;
            worker.Name = "CampusAutoLoginWorker";
            worker.Start();
        }

        protected override void OnStop()
        {
            stopEvent.Set();
            if (worker != null && worker.IsAlive) worker.Join(10000);
        }
    }

    internal static class ServiceManager
    {
        internal static bool Exists()
        {
            return ServiceController.GetServices().Any(s => String.Equals(s.ServiceName, AppPaths.ServiceName, StringComparison.OrdinalIgnoreCase));
        }

        internal static string State()
        {
            if (!Exists()) return "未安装";
            using (ServiceController service = new ServiceController(AppPaths.ServiceName))
            {
                try { return service.Status.ToString(); }
                catch { return "未知"; }
            }
        }

        internal static void Stop()
        {
            if (!Exists()) return;
            using (ServiceController service = new ServiceController(AppPaths.ServiceName))
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
                    service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }

        internal static void InstallOrUpdate()
        {
            string binPath = "\"" + AppPaths.InstalledExe + "\" --service";
            if (!Exists())
            {
                RunSc(new[] { "create", AppPaths.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "iQing 校园网自动登录" });
            }
            else
            {
                RunSc(new[] { "config", AppPaths.ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem" });
            }
            RunSc(new[] { "config", AppPaths.ServiceName, "depend=", "WlanSvc" });
            RunSc(new[] { "description", AppPaths.ServiceName, "在 Windows 登录前连接并认证 iQing 校园网。" });
            RunSc(new[] { "failure", AppPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000" });
        }

        internal static void Start()
        {
            using (ServiceController service = new ServiceController(AppPaths.ServiceName))
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running) return;
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            }
        }

        internal static void Restart()
        {
            Stop();
            Start();
        }

        internal static void Delete()
        {
            if (!Exists()) return;
            Stop();
            RunSc(new[] { "delete", AppPaths.ServiceName });
        }

        private static void RunSc(string[] arguments)
        {
            CommandResult result = CommandRunner.Run("sc.exe", arguments, null, 20000);
            if (result.ExitCode != 0)
            {
                string message = (result.Error + " " + result.Output).Trim();
                throw new InvalidOperationException("服务配置失败：" + message);
            }
        }
    }

    internal static class AppInstaller
    {
        private static void DisableLegacyScheduledTask()
        {
            try
            {
                CommandRunner.Run("schtasks.exe", new[] { "/End", "/TN", "Campus WiFi Auto Login" }, null, 10000);
            }
            catch { }
            try
            {
                CommandRunner.Run("schtasks.exe", new[] { "/Delete", "/TN", "Campus WiFi Auto Login", "/F" }, null, 10000);
            }
            catch { }
        }

        internal static void EnsureSecureDirectory()
        {
            DirectoryInfo directory = Directory.CreateDirectory(AppPaths.InstallDirectory);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }

        internal static void Install(string userName, string password, bool keepExistingPassword)
        {
            DisableLegacyScheduledTask();
            ServiceManager.Stop();
            EnsureSecureDirectory();
            string currentExe = Assembly.GetExecutingAssembly().Location;
            if (!String.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(AppPaths.InstalledExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(currentExe, AppPaths.InstalledExe, true);

            if (!keepExistingPassword) CredentialStore.Save(userName, password);
            WifiManager.EnsureIqingProfile();
            ServiceManager.InstallOrUpdate();
            SafeLog.Write("配置已保存，开机服务已安装。");
            ServiceManager.Start();
        }

        internal static void Uninstall()
        {
            ServiceManager.Delete();
            string currentExe = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            string installedExe = Path.GetFullPath(AppPaths.InstalledExe);
            if (!String.Equals(currentExe, installedExe, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(AppPaths.InstallDirectory)) Directory.Delete(AppPaths.InstallDirectory, true);
            }
            else
            {
                foreach (string file in Directory.GetFiles(AppPaths.InstallDirectory))
                {
                    if (!String.Equals(Path.GetFullPath(file), installedExe, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                ProcessStartInfo cleanup = new ProcessStartInfo();
                cleanup.FileName = "cmd.exe";
                cleanup.Arguments = "/d /c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + AppPaths.InstallDirectory + "\"";
                cleanup.UseShellExecute = false;
                cleanup.CreateNoWindow = true;
                Process.Start(cleanup);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox accountBox = new TextBox();
        private readonly TextBox passwordBox = new TextBox();
        private readonly Button installButton = new Button();
        private readonly Button testButton = new Button();
        private readonly Button uninstallButton = new Button();
        private readonly Label serviceState = new Label();
        private readonly RichTextBox logBox = new RichTextBox();
        private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();

        internal MainForm()
        {
            Text = "iQing 校园网自动登录";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 480);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Microsoft YaHei UI", 10F);
            BuildUi();
            Load += MainForm_Load;
            refreshTimer.Interval = 2000;
            refreshTimer.Tick += delegate { RefreshStatus(); };
        }

        private void BuildUi()
        {
            Label title = new Label();
            title.Text = "iQing 校园网自动登录";
            title.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
            title.Location = new Point(28, 22);
            title.AutoSize = true;
            Controls.Add(title);

            Label description = new Label();
            description.Text = "首次保存后，由 Windows 服务在登录界面出现前自动连接并认证。";
            description.ForeColor = Color.DimGray;
            description.Location = new Point(31, 66);
            description.Size = new Size(575, 28);
            Controls.Add(description);

            Label accountLabel = new Label();
            accountLabel.Text = "校园网账号";
            accountLabel.Location = new Point(31, 112);
            accountLabel.Size = new Size(100, 25);
            Controls.Add(accountLabel);
            accountBox.Location = new Point(140, 108);
            accountBox.Size = new Size(455, 27);
            Controls.Add(accountBox);

            Label passwordLabel = new Label();
            passwordLabel.Text = "校园网密码";
            passwordLabel.Location = new Point(31, 154);
            passwordLabel.Size = new Size(100, 25);
            Controls.Add(passwordLabel);
            passwordBox.Location = new Point(140, 150);
            passwordBox.Size = new Size(455, 27);
            passwordBox.UseSystemPasswordChar = true;
            Controls.Add(passwordBox);

            CheckBox preLogon = new CheckBox();
            preLogon.Text = "在 Windows 登录完成前自动认证（开机服务）";
            preLogon.Checked = true;
            preLogon.Enabled = false;
            preLogon.Location = new Point(140, 188);
            preLogon.Size = new Size(400, 28);
            Controls.Add(preLogon);

            installButton.Text = "保存并安装";
            installButton.Location = new Point(140, 226);
            installButton.Size = new Size(140, 38);
            installButton.BackColor = Color.FromArgb(35, 111, 219);
            installButton.ForeColor = Color.White;
            installButton.FlatStyle = FlatStyle.Flat;
            installButton.Click += InstallButton_Click;
            Controls.Add(installButton);

            testButton.Text = "立即检测";
            testButton.Location = new Point(294, 226);
            testButton.Size = new Size(140, 38);
            testButton.Click += TestButton_Click;
            Controls.Add(testButton);

            uninstallButton.Text = "卸载";
            uninstallButton.Location = new Point(448, 226);
            uninstallButton.Size = new Size(147, 38);
            uninstallButton.Click += UninstallButton_Click;
            Controls.Add(uninstallButton);

            serviceState.Location = new Point(31, 285);
            serviceState.Size = new Size(565, 28);
            serviceState.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            Controls.Add(serviceState);

            logBox.Location = new Point(31, 318);
            logBox.Size = new Size(564, 118);
            logBox.ReadOnly = true;
            logBox.BackColor = Color.FromArgb(247, 248, 250);
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.Font = new Font("Consolas", 9F);
            Controls.Add(logBox);

            Label security = new Label();
            security.Text = "账号密码使用 Windows DPAPI 机器级加密，仅 SYSTEM 和本机管理员可读取。";
            security.ForeColor = Color.DimGray;
            security.Location = new Point(31, 448);
            security.Size = new Size(590, 24);
            Controls.Add(security);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                StoredCredential stored = CredentialStore.Load();
                if (stored != null) accountBox.Text = stored.UserName;
            }
            catch { }
            RefreshStatus();
            refreshTimer.Start();
        }

        private void InstallButton_Click(object sender, EventArgs e)
        {
            string user = accountBox.Text.Trim();
            string password = passwordBox.Text;
            if (user.Length == 0)
            {
                MessageBox.Show("请输入校园网账号。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StoredCredential oldCredential = null;
            try { oldCredential = CredentialStore.Load(); } catch { }
            bool keepExisting = password.Length == 0 && oldCredential != null && oldCredential.UserName == user;
            if (password.Length == 0 && !keepExisting)
            {
                MessageBox.Show("请输入校园网密码。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true);
            try
            {
                AppInstaller.Install(user, password, keepExisting);
                passwordBox.Text = String.Empty;
                MessageBox.Show("安装成功。以后开机时会在 Windows 登录前自动尝试认证。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                RefreshStatus();
            }
        }

        private void TestButton_Click(object sender, EventArgs e)
        {
            SetBusy(true);
            try
            {
                if (!ServiceManager.Exists()) throw new InvalidOperationException("请先保存并安装服务。");
                ServiceManager.Restart();
                MessageBox.Show("已重新启动检测服务。连接 iQing 后，通常数秒内完成认证。", "已启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动检测失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                RefreshStatus();
            }
        }

        private void UninstallButton_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("确定卸载服务并删除已保存的账号密码吗？", "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SetBusy(true);
            try
            {
                AppInstaller.Uninstall();
                MessageBox.Show("已卸载服务并删除凭据。iQing Wi-Fi 配置仍保留，可继续手动使用。", "已卸载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            installButton.Enabled = !busy;
            testButton.Enabled = !busy;
            uninstallButton.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void RefreshStatus()
        {
            try
            {
                string state = ServiceManager.State();
                string ssid;
                try { ssid = WifiManager.GetCurrentSsid() ?? "未连接"; }
                catch { ssid = "无法读取"; }
                serviceState.Text = "服务状态：" + state + "    当前 Wi-Fi：" + ssid;
                serviceState.ForeColor = state == "Running" ? Color.SeaGreen : Color.DarkOrange;
                logBox.Text = SafeLog.Tail(6);
                logBox.SelectionStart = logBox.TextLength;
                logBox.ScrollToCaret();
            }
            catch (Exception ex)
            {
                serviceState.Text = "状态读取失败：" + ex.Message;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && String.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase))
            {
                ServiceBase.Run(new CampusLoginService());
                return 0;
            }

            if (args.Length > 0 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
            {
                StringBuilder reportBuilder = new StringBuilder();
                reportBuilder.AppendLine("Version=1.0.0");
                reportBuilder.AppendLine("OS=" + Environment.OSVersion);
                reportBuilder.AppendLine("Curl=" + File.Exists(Path.Combine(Environment.SystemDirectory, "curl.exe")));
                reportBuilder.AppendLine("ServiceMode=True");
                reportBuilder.AppendLine("PortalHost=10.1.7.3");
                try
                {
                    string ssid = WifiManager.GetCurrentSsid() ?? String.Empty;
                    string ip = WifiManager.GetWlanIpv4() ?? String.Empty;
                    reportBuilder.AppendLine("SSID=" + ssid);
                    reportBuilder.AppendLine("WlanIPv4=" + ip);
                    reportBuilder.AppendLine("PortalOnline=" + (ssid == AppPaths.WifiName && ip.Length > 0 && PortalClient.IsOnline(ip)));
                }
                catch (Exception ex)
                {
                    reportBuilder.AppendLine("NetworkSelfTestError=" + ex.Message);
                }
                reportBuilder.AppendLine("ExistingService=" + ServiceManager.State());
                string report = reportBuilder.ToString();
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "self-test.txt"), report, Encoding.UTF8);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
