using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace TaskBarHeroGuard
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (new Mutex(true, "TaskBarHeroGuard.SingleInstance", out created))
            {
                if (!created)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new GuardContext(args));
            }
        }
    }

    internal sealed class GuardContext : ApplicationContext
    {
        private readonly string[] _args;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _thresholdItem;
        private GuardConfig _config;
        private DateTime _nextRestartAllowedUtc = DateTime.MinValue;
        private long _lastObservedBytes;
        private DateTime _lastObservedUtc = DateTime.MinValue;

        public GuardContext(string[] args)
        {
            _args = args;
            _config = GuardConfig.Load(args);

            _statusItem = new ToolStripMenuItem("Status: starting");
            _statusItem.Enabled = false;

            _thresholdItem = new ToolStripMenuItem("");
            _thresholdItem.Enabled = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(_thresholdItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Check now", null, delegate { CheckOnce(true); });
            menu.Items.Add("Launch Task Bar Hero", null, delegate { LaunchGame(); });
            menu.Items.Add("Restart Task Bar Hero", null, delegate { RestartGame("Manual restart"); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open config", null, delegate { OpenConfigFile(); });
            menu.Items.Add("Reload config", null, delegate { ReloadConfig(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = SystemIcons.Shield;
            _notifyIcon.Text = "Task Bar Hero Guard";
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += delegate { CheckOnce(true); };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(1000, _config.CheckIntervalSeconds * 1000);
            _timer.Tick += delegate { CheckOnce(false); };
            _timer.Start();

            UpdateTrayText("monitoring");
            CheckOnce(false);
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _timer.Dispose();
            base.ExitThreadCore();
        }

        private void ReloadConfig(bool showBalloon)
        {
            _config = GuardConfig.Load(_args);
            _timer.Interval = Math.Max(1000, _config.CheckIntervalSeconds * 1000);
            UpdateTrayText("config reloaded");

            if (showBalloon)
            {
                ShowBalloon("Config reloaded", "Limit: " + FormatBytes(_config.ThresholdBytes));
            }
        }

        private void CheckOnce(bool showBalloon)
        {
            try
            {
                Process[] processes = GetTargetProcesses();
                if (processes.Length == 0)
                {
                    _lastObservedBytes = 0;
                    _lastObservedUtc = DateTime.UtcNow;
                    UpdateTrayText("not running");
                    if (_config.AutoLaunchWhenMissing)
                    {
                        LaunchGame();
                    }
                    else if (showBalloon)
                    {
                        ShowBalloon("Task Bar Hero is not running", "Launch is available from tray menu.");
                    }
                    return;
                }

                long totalBytes = 0;
                long maxBytes = 0;
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        long bytes = SafeWorkingSet(process);
                        totalBytes += bytes;
                        if (bytes > maxBytes)
                        {
                            maxBytes = bytes;
                        }
                    }
                }

                _lastObservedBytes = totalBytes;
                _lastObservedUtc = DateTime.UtcNow;
                UpdateTrayText("monitoring");

                if (maxBytes >= _config.ThresholdBytes)
                {
                    RestartGame("Memory limit exceeded: " + FormatBytes(maxBytes));
                    return;
                }

                if (showBalloon)
                {
                    ShowBalloon("Task Bar Hero monitoring", "Current: " + FormatBytes(totalBytes) + " / Limit: " + FormatBytes(_config.ThresholdBytes));
                }
            }
            catch (Exception ex)
            {
                UpdateTrayText("monitor error");
                ShowBalloon("Monitor error", ex.Message);
            }
        }

        private Process[] GetTargetProcesses()
        {
            string processName = Path.GetFileNameWithoutExtension(_config.ProcessName);
            return Process.GetProcessesByName(processName);
        }

        private static long SafeWorkingSet(Process process)
        {
            try
            {
                process.Refresh();
                return process.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }

        private void RestartGame(string reason)
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextRestartAllowedUtc)
            {
                return;
            }

            _nextRestartAllowedUtc = now.AddSeconds(_config.RestartCooldownSeconds);
            UpdateTrayText("restarting");
            ShowBalloon("Restarting Task Bar Hero", reason);

            Process[] processes = GetTargetProcesses();
            foreach (Process process in processes)
            {
                using (process)
                {
                    TryCloseOrKill(process);
                }
            }

            var restartTimer = new System.Windows.Forms.Timer();
            restartTimer.Interval = Math.Max(1000, _config.RestartDelaySeconds * 1000);
            restartTimer.Tick += delegate
            {
                restartTimer.Stop();
                restartTimer.Dispose();
                LaunchGame();
            };
            restartTimer.Start();
        }

        private void TryCloseOrKill(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    return;
                }

                if (process.CloseMainWindow())
                {
                    if (process.WaitForExit(_config.GracefulCloseSeconds * 1000))
                    {
                        return;
                    }
                }

                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }

        private void LaunchGame()
        {
            try
            {
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = _config.LaunchUri;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
                UpdateTrayText("launch requested");
            }
            catch (Exception ex)
            {
                UpdateTrayText("launch failed");
                ShowBalloon("Task Bar Hero launch failed", ex.Message);
            }
        }

        private void OpenConfigFile()
        {
            try
            {
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "notepad.exe";
                startInfo.Arguments = "\"" + GuardConfig.ConfigPath + "\"";
                startInfo.UseShellExecute = false;
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                ShowBalloon("Cannot open config", ex.Message);
            }
        }

        private void UpdateTrayText(string state)
        {
            string observed = _lastObservedUtc == DateTime.MinValue ? "-" : FormatBytes(_lastObservedBytes);
            _statusItem.Text = "Status: " + state + " / Current: " + observed;
            _thresholdItem.Text = "Limit: " + FormatBytes(_config.ThresholdBytes) + " / Interval: " + _config.CheckIntervalSeconds + "s";

            string text = "Task Bar Hero Guard - " + state;
            if (text.Length > 63)
            {
                text = text.Substring(0, 63);
            }

            _notifyIcon.Text = text;
        }

        private void ShowBalloon(string title, string text)
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.ShowBalloonTip(4000);
        }

        private static string FormatBytes(long bytes)
        {
            const double KiB = 1024.0;
            const double MiB = KiB * 1024.0;
            const double GiB = MiB * 1024.0;

            if (bytes >= (long)GiB)
            {
                return (bytes / GiB).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= (long)MiB)
            {
                return (bytes / MiB).ToString("0", CultureInfo.InvariantCulture) + " MB";
            }

            if (bytes >= (long)KiB)
            {
                return (bytes / KiB).ToString("0", CultureInfo.InvariantCulture) + " KB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }

    internal sealed class GuardConfig
    {
        public const long DefaultThresholdBytes = 1024L * 1024L * 1024L;
        public static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TaskBarHeroGuard.ini");

        public string ProcessName = "TaskBarHero.exe";
        public string LaunchUri = "steam://rungameid/3678970";
        public long ThresholdBytes = DefaultThresholdBytes;
        public int CheckIntervalSeconds = 10;
        public int RestartDelaySeconds = 8;
        public int RestartCooldownSeconds = 120;
        public int GracefulCloseSeconds = 10;
        public bool AutoLaunchWhenMissing = false;

        public static GuardConfig Load(string[] args)
        {
            var config = new GuardConfig();
            EnsureConfigFile();

            foreach (KeyValuePair<string, string> pair in ReadPairs(ConfigPath))
            {
                config.Apply(pair.Key, pair.Value);
            }

            foreach (string arg in args)
            {
                string normalized = arg.TrimStart('-', '/');
                int equals = normalized.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                config.Apply(normalized.Substring(0, equals), normalized.Substring(equals + 1));
            }

            config.Normalize();
            return config;
        }

        private static void EnsureConfigFile()
        {
            if (File.Exists(ConfigPath))
            {
                return;
            }

            File.WriteAllText(ConfigPath,
                "# Task Bar Hero Guard settings\r\n" +
                "ProcessName=TaskBarHero.exe\r\n" +
                "LaunchUri=steam://rungameid/3678970\r\n" +
                "ThresholdMB=1024\r\n" +
                "CheckIntervalSeconds=10\r\n" +
                "RestartDelaySeconds=8\r\n" +
                "RestartCooldownSeconds=120\r\n" +
                "GracefulCloseSeconds=10\r\n" +
                "AutoLaunchWhenMissing=false\r\n");
        }

        private static IEnumerable<KeyValuePair<string, string>> ReadPairs(string path)
        {
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                yield return new KeyValuePair<string, string>(line.Substring(0, equals).Trim(), line.Substring(equals + 1).Trim());
            }
        }

        private void Apply(string key, string value)
        {
            string normalizedKey = key.Trim().ToLowerInvariant();
            if (normalizedKey == "processname" || normalizedKey == "process")
            {
                ProcessName = value;
            }
            else if (normalizedKey == "launchuri" || normalizedKey == "steamuri")
            {
                LaunchUri = value;
            }
            else if (normalizedKey == "thresholdmb")
            {
                ThresholdBytes = ParseLong(value, 1024) * 1024L * 1024L;
            }
            else if (normalizedKey == "thresholdbytes")
            {
                ThresholdBytes = ParseLong(value, DefaultThresholdBytes);
            }
            else if (normalizedKey == "checkintervalseconds" || normalizedKey == "interval")
            {
                CheckIntervalSeconds = ParseInt(value, CheckIntervalSeconds);
            }
            else if (normalizedKey == "restartdelayseconds")
            {
                RestartDelaySeconds = ParseInt(value, RestartDelaySeconds);
            }
            else if (normalizedKey == "restartcooldownseconds")
            {
                RestartCooldownSeconds = ParseInt(value, RestartCooldownSeconds);
            }
            else if (normalizedKey == "gracefulcloseseconds")
            {
                GracefulCloseSeconds = ParseInt(value, GracefulCloseSeconds);
            }
            else if (normalizedKey == "autolaunchwhenmissing")
            {
                AutoLaunchWhenMissing = ParseBool(value, AutoLaunchWhenMissing);
            }
        }

        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ProcessName))
            {
                ProcessName = "TaskBarHero.exe";
            }

            if (string.IsNullOrWhiteSpace(LaunchUri))
            {
                LaunchUri = "steam://rungameid/3678970";
            }

            if (ThresholdBytes <= 0)
            {
                ThresholdBytes = DefaultThresholdBytes;
            }

            CheckIntervalSeconds = Clamp(CheckIntervalSeconds, 1, 3600);
            RestartDelaySeconds = Clamp(RestartDelaySeconds, 1, 300);
            RestartCooldownSeconds = Clamp(RestartCooldownSeconds, 10, 3600);
            GracefulCloseSeconds = Clamp(GracefulCloseSeconds, 0, 300);
        }

        private static long ParseLong(string value, long fallback)
        {
            long parsed;
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            if (bool.TryParse(value, out parsed))
            {
                return parsed;
            }

            if (value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value == "0" || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return fallback;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
