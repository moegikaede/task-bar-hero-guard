using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
        private readonly System.Windows.Forms.Timer _startupWindowTimer;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripLabel _statusItem;
        private readonly ToolStripLabel _thresholdItem;
        private GuardConfig _config;
        private DateTime _nextRestartAllowedUtc = DateTime.MinValue;
        private long _lastObservedBytes;
        private DateTime _lastObservedUtc = DateTime.MinValue;
        private bool _restartPending;
        private string _pendingReason = "";
        private DateTime _pendingSinceUtc = DateTime.MinValue;
        private long _pendingLogPosition;
        private DateTime _pendingSaveWriteUtc = DateTime.MinValue;
        private DateTime _stageSignalSeenUtc = DateTime.MinValue;
        private bool _pendingRestartMustWaitForStage;
        private DateTime _sendStartupWindowToBackUntilUtc = DateTime.MinValue;
        private DateTime _bringGameToFrontUntilUtc = DateTime.MinValue;
        private DateTime _bringGameToFrontRetryUntilUtc = DateTime.MinValue;
        private DateTime _autoCloseOfflineRewardUntilUtc = DateTime.MinValue;
        private DateTime _nextAutoCloseOfflineRewardCheckUtc = DateTime.MinValue;
        private DateTime _restoreRestartWindowPositionUntilUtc = DateTime.MinValue;
        private DateTime _keepGameTopMostUntilUtc = DateTime.MinValue;
        private readonly Dictionary<IntPtr, bool> _temporaryTopMostWindows = new Dictionary<IntPtr, bool>();
        private readonly HashSet<IntPtr> _startupWindowsSentToBack = new HashSet<IntPtr>();
        private Rect _restartWindowRect;
        private long _stageStartLogPosition;
        private bool _startupWindowMoved;
        private bool _waitingForStageStartToFront;
        private bool _retryBringGameToFront;
        private bool _autoCloseOfflineReward;
        private bool _restoreRestartWindowPosition;
        private bool _targetWasRunning;
        private bool _startupHandlingInitializedForLaunch;

        private static readonly IntPtr HwndTop = new IntPtr(0);
        private static readonly IntPtr HwndBottom = new IntPtr(1);
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);
        private static readonly IntPtr HwndNoTopMost = new IntPtr(-2);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoOwnerZOrder = 0x0200;
        private const int SwRestore = 9;
        private const int GwlExStyle = -20;
        private const int WsExTopMost = 0x00000008;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;

        public GuardContext(string[] args)
        {
            _args = args;
            _config = GuardConfig.Load(args);

            _startupItem = new ToolStripMenuItem("スタートアップ登録");
            _startupItem.CheckOnClick = false;
            _startupItem.Click += delegate { ToggleStartupRegistration(); };

            // Status rows are informational labels, not disabled commands. Labels retain the
            // normal menu text color without becoming selectable or looking unavailable.
            _statusItem = new ToolStripLabel("Status: starting");
            _statusItem.ForeColor = SystemColors.MenuText;

            _thresholdItem = new ToolStripLabel("");
            _thresholdItem.ForeColor = SystemColors.MenuText;

            var menu = new ContextMenuStrip();
            menu.Opening += delegate { UpdateStartupMenuItem(); };
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_statusItem);
            menu.Items.Add(_thresholdItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Launch Task Bar Hero", null, delegate { LaunchGame(); });
            menu.Items.Add("Send startup windows to back", null, delegate { SendStartupWindowsToBack(true); });
            menu.Items.Add("Bring Task Bar Hero to front", null, delegate { BringGameWindowsToFront(true); });
            menu.Items.Add("Restart Task Bar Hero", null, delegate { RequestManualRestart(); });
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

            _startupWindowTimer = new System.Windows.Forms.Timer();
            _startupWindowTimer.Interval = 500;
            _startupWindowTimer.Tick += delegate { TickStartupWindowBack(); };

            UpdateTrayText("monitoring");
            CheckOnce(false);
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _startupWindowTimer.Stop();
            ReleaseGameTopMostHold();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _startupWindowTimer.Dispose();
            _timer.Dispose();
            base.ExitThreadCore();
        }

        private void ReloadConfig(bool showBalloon)
        {
            _config = GuardConfig.Load(_args);
            _timer.Interval = Math.Max(1000, _config.CheckIntervalSeconds * 1000);
            ResetPendingRestart();
            UpdateTrayText("config reloaded");

            if (showBalloon)
            {
                ShowBalloon("Config reloaded", "Limit: " + FormatBytes(_config.ThresholdBytes) + " / Hard: " + FormatBytes(_config.HardThresholdBytes));
            }
        }

        private void CheckOnce(bool showBalloon)
        {
            try
            {
                Process[] processes = GetTargetProcesses();
                if (processes.Length == 0)
                {
                    bool targetStoppedSinceLastCheck = _targetWasRunning;
                    _targetWasRunning = false;
                    if (targetStoppedSinceLastCheck)
                    {
                        // Only a real running-to-stopped transition opens a new external launch.
                        // Steam's expected startup gap must preserve the launch already initialized.
                        _startupHandlingInitializedForLaunch = false;
                    }
                    _lastObservedBytes = 0;
                    _lastObservedUtc = DateTime.UtcNow;
                    ResetPendingRestart();
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

                if (!_targetWasRunning)
                {
                    _targetWasRunning = true;
                    BeginStartupWindowHandling();
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

                if (_restartPending)
                {
                    EvaluatePendingRestart(maxBytes);
                    return;
                }

                UpdateTrayText("monitoring");

                if (maxBytes >= _config.HardThresholdBytes)
                {
                    RestartGame("Hard memory limit exceeded: " + FormatBytes(maxBytes));
                    return;
                }

                if (maxBytes >= _config.ThresholdBytes)
                {
                    if (_config.WaitForStageLog)
                    {
                        BeginPendingRestart("Memory limit exceeded: " + FormatBytes(maxBytes), false);
                    }
                    else
                    {
                        RestartGame("Memory limit exceeded: " + FormatBytes(maxBytes));
                    }

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

        private void RequestManualRestart()
        {
            if (_restartPending)
            {
                ShowBalloon("Restart already pending", "Waiting for the current stage to finish.");
                return;
            }

            Process[] processes = GetTargetProcesses();
            bool isRunning = processes.Length > 0;
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            if (!isRunning)
            {
                ShowBalloon("Task Bar Hero is not running", "There is no running game to restart.");
                return;
            }

            // A tray restart is an explicit safe-point request. Unlike memory protection,
            // it must never force a restart before the stage completion and save are observed.
            BeginPendingRestart("Manual restart requested", true);
        }

        private void BeginPendingRestart(string reason, bool mustWaitForStage)
        {
            _restartPending = true;
            _pendingReason = reason;
            _pendingRestartMustWaitForStage = mustWaitForStage;
            _pendingSinceUtc = DateTime.UtcNow;
            _pendingLogPosition = GetFileLength(_config.PlayerLogPath);
            _pendingSaveWriteUtc = GetLastWriteUtc(_config.SaveFilePath);
            _stageSignalSeenUtc = DateTime.MinValue;

            UpdateTrayText("restart pending");
            ShowBalloon("Restart pending", "Waiting for stage log. " + reason);
        }

        private void EvaluatePendingRestart(long maxBytes)
        {
            DateTime now = DateTime.UtcNow;
            UpdateTrayText("restart pending");

            if (!_pendingRestartMustWaitForStage && maxBytes >= _config.HardThresholdBytes)
            {
                RestartGame("Hard memory limit exceeded while pending: " + FormatBytes(maxBytes));
                return;
            }

            if (!_pendingRestartMustWaitForStage && (now - _pendingSinceUtc).TotalSeconds >= _config.MaxDeferralSeconds)
            {
                RestartGame("Max deferral reached. " + _pendingReason);
                return;
            }

            if (_stageSignalSeenUtc == DateTime.MinValue && TryConsumeStageSignal())
            {
                _stageSignalSeenUtc = now;
            }

            if (_stageSignalSeenUtc == DateTime.MinValue)
            {
                return;
            }

            DateTime saveWriteUtc = GetLastWriteUtc(_config.SaveFilePath);
            bool saveUpdated = saveWriteUtc > _pendingSaveWriteUtc;
            bool signalSettled = (now - _stageSignalSeenUtc).TotalSeconds >= _config.StageSignalSettleSeconds;
            bool saveSettled = saveUpdated && (now - saveWriteUtc).TotalSeconds >= _config.SaveSettleSeconds;

            if ((_config.RequireSaveUpdate && saveSettled) || (!_config.RequireSaveUpdate && signalSettled))
            {
                RestartGame("Stage completion signal detected. " + _pendingReason);
            }
        }

        private bool TryConsumeStageSignal()
        {
            string chunk = ReadLogChunk(_config.PlayerLogPath, ref _pendingLogPosition, _config.MaxLogReadBytes);
            if (chunk.Length == 0)
            {
                return false;
            }

            return ContainsConfiguredSignal(chunk, _config.StageLogSignalText1)
                && ContainsConfiguredSignal(chunk, _config.StageLogSignalText2);
        }

        private void ResetPendingRestart()
        {
            _restartPending = false;
            _pendingReason = "";
            _pendingRestartMustWaitForStage = false;
            _pendingSinceUtc = DateTime.MinValue;
            _pendingLogPosition = GetFileLength(_config.PlayerLogPath);
            _pendingSaveWriteUtc = GetLastWriteUtc(_config.SaveFilePath);
            _stageSignalSeenUtc = DateTime.MinValue;
        }

        private static long GetFileLength(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static long GetRecentFileStartPosition(string path, int maxBytes)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    return 0;
                }

                return Math.Max(0, file.Length - Math.Max(4096, maxBytes));
            }
            catch
            {
                return 0;
            }
        }

        private static DateTime GetLastWriteUtc(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static string ReadLogChunk(string path, ref long position, int maxBytes)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    position = 0;
                    return "";
                }

                if (file.Length < position)
                {
                    position = 0;
                }

                long available = file.Length - position;
                if (available <= 0)
                {
                    return "";
                }

                if (available > maxBytes)
                {
                    position = file.Length - maxBytes;
                    available = maxBytes;
                }

                byte[] bytes = new byte[available];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Position = position;
                    int read = stream.Read(bytes, 0, bytes.Length);
                    position = stream.Position;
                    return System.Text.Encoding.UTF8.GetString(bytes, 0, read);
                }
            }
            catch
            {
                return "";
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
            RestartGame(reason, false);
        }

        private void RestartGame(string reason, bool ignoreCooldown)
        {
            DateTime now = DateTime.UtcNow;
            if (!ignoreCooldown && now < _nextRestartAllowedUtc)
            {
                return;
            }

            _nextRestartAllowedUtc = now.AddSeconds(_config.RestartCooldownSeconds);
            ResetPendingRestart();
            RememberRestartWindowPosition();
            ReleaseGameTopMostHold();
            // RestartGame owns the next launch, so discard only the previous launch generation.
            _startupHandlingInitializedForLaunch = false;
            _targetWasRunning = false;
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
                BeginStartupWindowHandling();
                UpdateTrayText("launch requested");
            }
            catch (Exception ex)
            {
                UpdateTrayText("launch failed");
                ShowBalloon("Task Bar Hero launch failed", ex.Message);
            }
        }

        private void BeginStartupWindowHandling()
        {
            DateTime now = DateTime.UtcNow;
            if (_startupHandlingInitializedForLaunch)
            {
                return;
            }

            // Steam launch and process discovery describe the same launch. Initializing twice
            // loses both handled window identities and unread stage-log position.
            _startupHandlingInitializedForLaunch = true;
            _startupWindowMoved = false;
            // A new launch has a new sequence of Steam, splash, and Unity window handles.
            _startupWindowsSentToBack.Clear();
            _waitingForStageStartToFront = _config.BringGameToFrontOnStageStart;
            _retryBringGameToFront = false;
            _autoCloseOfflineReward = _config.AutoCloseOfflineReward;
            int restoreWindowPositionSeconds = _config.RestoreRestartWindowPositionSeconds;
            if (_config.BringGameToFrontOnStageStart)
            {
                restoreWindowPositionSeconds = Math.Max(restoreWindowPositionSeconds, _config.StageStartFrontWaitSeconds);
            }

            _restoreRestartWindowPositionUntilUtc = _restoreRestartWindowPosition
                ? now.AddSeconds(restoreWindowPositionSeconds)
                : DateTime.MinValue;
            _stageStartLogPosition = GetRecentFileStartPosition(_config.PlayerLogPath, _config.MaxLogReadBytes);
            _sendStartupWindowToBackUntilUtc = _config.SendStartupWindowToBack
                ? now.AddSeconds(_config.StartupWindowBackDurationSeconds)
                : DateTime.MinValue;
            _bringGameToFrontUntilUtc = _config.BringGameToFrontOnStageStart
                ? now.AddSeconds(_config.StageStartFrontWaitSeconds)
                : DateTime.MinValue;
            _nextAutoCloseOfflineRewardCheckUtc = _config.AutoCloseOfflineReward
                ? now.AddSeconds(_config.AutoCloseOfflineRewardDelaySeconds)
                : DateTime.MinValue;
            _autoCloseOfflineRewardUntilUtc = _config.AutoCloseOfflineReward
                ? _nextAutoCloseOfflineRewardCheckUtc.AddSeconds(_config.AutoCloseOfflineRewardDurationSeconds)
                : DateTime.MinValue;

            if ((_config.SendStartupWindowToBack || _config.BringGameToFrontOnStageStart || _config.AutoCloseOfflineReward || _restoreRestartWindowPosition) && !_startupWindowTimer.Enabled)
            {
                _startupWindowTimer.Start();
            }
        }

        private void TickStartupWindowBack()
        {
            DateTime now = DateTime.UtcNow;

            if (_config.SendStartupWindowToBack && now <= _sendStartupWindowToBackUntilUtc)
            {
                SendStartupWindowsToBack(false);
            }

            if (_waitingForStageStartToFront)
            {
                if (now > _bringGameToFrontUntilUtc)
                {
                    _waitingForStageStartToFront = false;
                }
                else if (TryConsumeStageStartSignal())
                {
                    _waitingForStageStartToFront = false;
                    // Unity can replace or reposition its final window well after the process first appears.
                    // Restart the correction window from the stage signal so the gameplay window is the target.
                    ExtendRestartWindowPositionRestore(
                        now,
                        Math.Max(_config.RestoreRestartWindowPositionSeconds, _config.RestoreRestartWindowPositionAfterStageStartSeconds));
                    StopStartupWindowBack();
                    BeginBringGameToFrontRetry();
                }
            }

            if (_retryBringGameToFront)
            {
                if (now > _bringGameToFrontRetryUntilUtc)
                {
                    _retryBringGameToFront = false;
                }
                else
                {
                    BringGameWindowsToFront(false);
                }
            }

            if (_autoCloseOfflineReward)
            {
                if (now > _autoCloseOfflineRewardUntilUtc)
                {
                    StopAutoCloseOfflineReward();
                }
                else if (now >= _nextAutoCloseOfflineRewardCheckUtc)
                {
                    // A physical click can still miss a Unity frame boundary. End monitoring only
                    // after a fresh capture proves the detected popup has disappeared.
                    if (TryAutoCloseOfflineReward())
                    {
                        StopAutoCloseOfflineReward();
                    }
                    else
                    {
                        _nextAutoCloseOfflineRewardCheckUtc = now.AddMilliseconds(_config.AutoCloseOfflineRewardIntervalMilliseconds);
                    }
                }
            }

            if (_restoreRestartWindowPosition)
            {
                if (now > _restoreRestartWindowPositionUntilUtc)
                {
                    StopRestoreRestartWindowPosition();
                }
                else
                {
                    TryRestoreRestartWindowPosition();
                }
            }

            if (_keepGameTopMostUntilUtc != DateTime.MinValue && now > _keepGameTopMostUntilUtc)
            {
                ReleaseGameTopMostHold();
            }
            else if (_keepGameTopMostUntilUtc != DateTime.MinValue)
            {
                MaintainGameTopMostHold();
            }

            if (now > _sendStartupWindowToBackUntilUtc && !_waitingForStageStartToFront && !_retryBringGameToFront && !_autoCloseOfflineReward && !_restoreRestartWindowPosition && _keepGameTopMostUntilUtc == DateTime.MinValue)
            {
                _startupWindowTimer.Stop();
                if (_startupWindowMoved)
                {
                    UpdateTrayText("monitoring");
                }
            }
        }

        private void BeginBringGameToFrontRetry()
        {
            _retryBringGameToFront = true;
            _bringGameToFrontRetryUntilUtc = DateTime.UtcNow.AddSeconds(_config.StageStartFrontRetrySeconds);
            BringGameWindowsToFront(false);
        }

        private void StopStartupWindowBack()
        {
            _sendStartupWindowToBackUntilUtc = DateTime.MinValue;
            _startupWindowMoved = false;
        }

        private void StopAutoCloseOfflineReward()
        {
            _autoCloseOfflineReward = false;
            _autoCloseOfflineRewardUntilUtc = DateTime.MinValue;
            _nextAutoCloseOfflineRewardCheckUtc = DateTime.MinValue;
        }

        private void RememberRestartWindowPosition()
        {
            _restoreRestartWindowPosition = false;
            _restartWindowRect = new Rect();
            _restoreRestartWindowPositionUntilUtc = DateTime.MinValue;

            if (!_config.RestoreRestartWindowPosition)
            {
                return;
            }

            IntPtr hWnd = FindProcessWindow(_config.ProcessName, "UnityWndClass");
            Rect rect;
            if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out rect))
            {
                return;
            }

            if ((rect.Right - rect.Left) <= 0 || (rect.Bottom - rect.Top) <= 0)
            {
                return;
            }

            _restartWindowRect = rect;
            _restoreRestartWindowPosition = true;
        }

        private void StopRestoreRestartWindowPosition()
        {
            _restoreRestartWindowPosition = false;
            _restoreRestartWindowPositionUntilUtc = DateTime.MinValue;
        }

        private void ExtendRestartWindowPositionRestore(DateTime now, int seconds)
        {
            if (!_restoreRestartWindowPosition || seconds <= 0)
            {
                return;
            }

            DateTime restoreUntil = now.AddSeconds(seconds);
            if (restoreUntil > _restoreRestartWindowPositionUntilUtc)
            {
                _restoreRestartWindowPositionUntilUtc = restoreUntil;
            }
        }

        private bool TryRestoreRestartWindowPosition()
        {
            IntPtr hWnd = FindProcessWindow(_config.ProcessName, "UnityWndClass");
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            Rect current;
            if (!GetWindowRect(hWnd, out current))
            {
                return false;
            }

            int tolerance = _config.RestartWindowPositionTolerancePixels;
            int dx = Math.Abs(current.Left - _restartWindowRect.Left);
            int dy = Math.Abs(current.Top - _restartWindowRect.Top);
            if (dx <= tolerance && dy <= tolerance)
            {
                return true;
            }

            int width = current.Right - current.Left;
            int height = current.Bottom - current.Top;
            if (width <= 0 || height <= 0)
            {
                width = _restartWindowRect.Right - _restartWindowRect.Left;
                height = _restartWindowRect.Bottom - _restartWindowRect.Top;
            }

            return SetWindowPos(
                hWnd,
                IntPtr.Zero,
                _restartWindowRect.Left,
                _restartWindowRect.Top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        }

        private bool TryConsumeStageStartSignal()
        {
            string chunk = ReadLogChunk(_config.PlayerLogPath, ref _stageStartLogPosition, _config.MaxLogReadBytes);
            if (chunk.Length == 0)
            {
                return false;
            }

            return ContainsSignal(chunk, _config.StageStartLogSignalText1, _config.StageStartLogSignalText2);
        }

        private static bool ContainsSignal(string chunk, string signal1, string signal2)
        {
            if (!ContainsConfiguredSignal(chunk, signal1))
            {
                return false;
            }

            return string.IsNullOrEmpty(signal2) || ContainsConfiguredSignal(chunk, signal2);
        }

        private static bool ContainsConfiguredSignal(string chunk, string signal)
        {
            if (string.IsNullOrEmpty(signal))
            {
                return false;
            }

            int wildcard = signal.IndexOf('*');
            if (wildcard < 0)
            {
                return chunk.IndexOf(signal, StringComparison.Ordinal) >= 0;
            }

            string prefix = signal.Substring(0, wildcard);
            string suffix = signal.Substring(wildcard + 1);
            using (var reader = new StringReader(chunk))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int prefixIndex = line.IndexOf(prefix, StringComparison.Ordinal);
                    if (prefixIndex >= 0
                        && line.IndexOf(suffix, prefixIndex + prefix.Length, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void SendStartupWindowsToBack(bool showBalloon)
        {
            int moved = 0;

            // Reordering the same handle every timer tick fights applications that activate themselves.
            // Each window generation is sent back once; replacement Unity windows are still handled.
            moved += MoveWindowsForProcessNamesToBack(new string[] { _config.ProcessName }, new string[0], false, _startupWindowsSentToBack);
            moved += MoveWindowsForProcessNamesToBack(SplitConfigList(_config.StartupBackProcessNames), SplitConfigList(_config.StartupBackWindowTitleContains), _config.StartupBackRequireTitleMatch, _startupWindowsSentToBack);

            if (moved > 0)
            {
                _startupWindowMoved = true;
                UpdateTrayText("startup windows sent to back");
            }
            else if (showBalloon)
            {
                ShowBalloon("Startup window not found", "No visible game or Steam launch window is ready yet.");
            }
        }

        private void BringGameWindowsToFront(bool showBalloon)
        {
            StopStartupWindowBack();
            bool keepTopMost = _config.KeepGameTopMostAfterStageStart;
            IntPtr hWnd = FindProcessWindow(_config.ProcessName, "UnityWndClass");
            bool originalWasTopMost;
            if (hWnd != IntPtr.Zero && TryBringWindowToFront(hWnd, keepTopMost, out originalWasTopMost))
            {
                if (keepTopMost)
                {
                    RememberGameTopMostState(hWnd, originalWasTopMost);
                    // Keep the gameplay window topmost for the whole run. A fixed timeout lets it
                    // fall behind unrelated windows even though the option remains enabled.
                    _keepGameTopMostUntilUtc = DateTime.MaxValue;
                    MaintainGameTopMostHold();
                    if (!_startupWindowTimer.Enabled)
                    {
                        _startupWindowTimer.Start();
                    }
                }

                UpdateTrayText("game brought to front");
            }
            else if (showBalloon)
            {
                ShowBalloon("Task Bar Hero window not found", "No visible game window is ready yet.");
            }
        }

        private void MaintainGameTopMostHold()
        {
            IntPtr hWnd = FindProcessWindow(_config.ProcessName, "UnityWndClass");
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            RememberGameTopMostState(hWnd, IsWindowTopMost(hWnd));
            SetWindowPos(hWnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
        }

        private void RememberGameTopMostState(IntPtr hWnd, bool originalWasTopMost)
        {
            if (hWnd != IntPtr.Zero && !_temporaryTopMostWindows.ContainsKey(hWnd))
            {
                _temporaryTopMostWindows[hWnd] = originalWasTopMost;
            }
        }

        private void ReleaseGameTopMostHold()
        {
            foreach (KeyValuePair<IntPtr, bool> window in new List<KeyValuePair<IntPtr, bool>>(_temporaryTopMostWindows))
            {
                try
                {
                    if (!window.Value && IsWindow(window.Key) && IsWindowTopMost(window.Key))
                    {
                        SetWindowPos(window.Key, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
                    }
                }
                catch
                {
                }
            }

            _temporaryTopMostWindows.Clear();
            _keepGameTopMostUntilUtc = DateTime.MinValue;
        }

        private bool TryAutoCloseOfflineReward()
        {
            IntPtr hWnd = FindProcessWindow(_config.ProcessName, "UnityWndClass");
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            Point clickPoint;
            using (Bitmap image = CaptureWindow(hWnd))
            {
                if (image == null || !TryFindOfflineRewardCloseButton(image, out clickPoint))
                {
                    return false;
                }
            }

            // Unity ignores synthetic background button messages in this screen. Stop the competing
            // Z-order loop before the verified real click, then return the startup window once.
            StopStartupWindowBack();
            bool clicked = TryRealClick(hWnd, clickPoint.X, clickPoint.Y);
            if (clicked)
            {
                UpdateTrayText("offline reward closed");
                TrySendWindowToBack(hWnd);
                Thread.Sleep(400);

                using (Bitmap verification = CaptureWindow(hWnd))
                {
                    Point remainingButton;
                    return verification != null && !TryFindOfflineRewardCloseButton(verification, out remainingButton);
                }
            }

            return false;
        }

        private static int MoveWindowsForProcessNamesToBack(string[] processNames, string[] titleTokens, bool requireTitleMatch, HashSet<IntPtr> handledWindows)
        {
            return MoveWindowsForProcessNames(processNames, titleTokens, requireTitleMatch, true, false, null, handledWindows);
        }

        private static int MoveWindowsForProcessNamesToFront(string[] processNames, string[] titleTokens, bool requireTitleMatch)
        {
            return MoveWindowsForProcessNames(processNames, titleTokens, requireTitleMatch, false, false, null);
        }

        private static int MoveWindowsForProcessNamesToFront(string[] processNames, string[] titleTokens, bool requireTitleMatch, bool keepTopMost, Dictionary<IntPtr, bool> temporaryTopMostWindows)
        {
            return MoveWindowsForProcessNames(processNames, titleTokens, requireTitleMatch, false, keepTopMost, temporaryTopMostWindows);
        }

        private static int MoveWindowsForProcessNames(string[] processNames, string[] titleTokens, bool requireTitleMatch, bool toBack)
        {
            return MoveWindowsForProcessNames(processNames, titleTokens, requireTitleMatch, toBack, false, null, null);
        }

        private static int MoveWindowsForProcessNames(string[] processNames, string[] titleTokens, bool requireTitleMatch, bool toBack, bool keepTopMost, Dictionary<IntPtr, bool> temporaryTopMostWindows)
        {
            return MoveWindowsForProcessNames(processNames, titleTokens, requireTitleMatch, toBack, keepTopMost, temporaryTopMostWindows, null);
        }

        private static int MoveWindowsForProcessNames(string[] processNames, string[] titleTokens, bool requireTitleMatch, bool toBack, bool keepTopMost, Dictionary<IntPtr, bool> temporaryTopMostWindows, HashSet<IntPtr> handledWindows)
        {
            WindowMatchState state = new WindowMatchState();
            state.ProcessNames = BuildProcessNameSet(processNames);
            state.TitleTokens = titleTokens;
            state.RequireTitleMatch = requireTitleMatch;
            state.ToBack = toBack;
            state.KeepTopMost = keepTopMost;
            state.TemporaryTopMostWindows = temporaryTopMostWindows;
            state.HandledWindows = handledWindows;
            state.Moved = 0;

            if (state.ProcessNames.Count == 0)
            {
                return 0;
            }

            foreach (string processName in state.ProcessNames.Keys)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    using (process)
                    {
                        try
                        {
                            int targetProcessId = process.Id;
                            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                            {
                                int windowProcessId;
                                GetWindowThreadProcessId(hWnd, out windowProcessId);
                                if (windowProcessId != targetProcessId)
                                {
                                    return true;
                                }

                                if (!TryMoveMatchedWindow(hWnd, state))
                                {
                                    return true;
                                }

                                return true;
                            }, IntPtr.Zero);

                            foreach (ProcessThread thread in process.Threads)
                            {
                                EnumThreadWindows((uint)thread.Id, delegate(IntPtr hWnd, IntPtr lParam)
                                {
                                    TryMoveMatchedWindow(hWnd, state);
                                    return true;
                                }, IntPtr.Zero);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return state.Moved;
        }

        private static bool TryMoveMatchedWindow(IntPtr hWnd, WindowMatchState state)
        {
            if (hWnd == IntPtr.Zero || (!IsWindowVisible(hWnd) && state.ToBack))
            {
                return false;
            }

            if (state.ToBack && state.HandledWindows != null && state.HandledWindows.Contains(hWnd))
            {
                return false;
            }

            string title = GetWindowTitle(hWnd);
            string className = GetWindowClassName(hWnd);
            if (IsIgnoredWindowClass(className))
            {
                return false;
            }

            if (state.RequireTitleMatch && !TitleMatches(title, state.TitleTokens))
            {
                return false;
            }

            bool originalWasTopMost = false;
            if (state.ToBack ? TrySendWindowToBack(hWnd) : TryBringWindowToFront(hWnd, state.KeepTopMost, out originalWasTopMost))
            {
                if (state.KeepTopMost && state.TemporaryTopMostWindows != null && !state.TemporaryTopMostWindows.ContainsKey(hWnd))
                {
                    state.TemporaryTopMostWindows[hWnd] = originalWasTopMost;
                }

                if (state.ToBack && state.HandledWindows != null)
                {
                    state.HandledWindows.Add(hWnd);
                }

                state.Moved++;
                return true;
            }

            return false;
        }

        private static bool IsIgnoredWindowClass(string className)
        {
            return className.Equals("IME", StringComparison.OrdinalIgnoreCase)
                || className.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase);
        }

        private static IntPtr FindProcessWindow(string processName, string requiredClassName)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName));
            }
            catch
            {
                return IntPtr.Zero;
            }

            foreach (Process process in processes)
            {
                using (process)
                {
                    WindowFindState state = new WindowFindState();
                    state.ProcessId = process.Id;
                    state.RequiredClassName = requiredClassName ?? "";
                    state.Handle = IntPtr.Zero;

                    EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                    {
                        int windowProcessId;
                        GetWindowThreadProcessId(hWnd, out windowProcessId);
                        if (windowProcessId != state.ProcessId || !IsWindowVisible(hWnd))
                        {
                            return true;
                        }

                        string className = GetWindowClassName(hWnd);
                        if (IsIgnoredWindowClass(className))
                        {
                            return true;
                        }

                        if (state.RequiredClassName.Length > 0 && !className.Equals(state.RequiredClassName, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        state.Handle = hWnd;
                        return false;
                    }, IntPtr.Zero);

                    if (state.Handle != IntPtr.Zero)
                    {
                        return state.Handle;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static Bitmap CaptureWindow(IntPtr hWnd)
        {
            Rect rect;
            if (!GetWindowRect(hWnd, out rect))
            {
                return null;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            Bitmap bitmap = null;
            Graphics graphics = null;
            IntPtr hdc = IntPtr.Zero;
            try
            {
                bitmap = new Bitmap(width, height);
                graphics = Graphics.FromImage(bitmap);
                hdc = graphics.GetHdc();
                if (!PrintWindow(hWnd, hdc, 0))
                {
                    bitmap.Dispose();
                    bitmap = null;
                }

                return bitmap;
            }
            catch
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }

                return null;
            }
            finally
            {
                if (graphics != null)
                {
                    if (hdc != IntPtr.Zero)
                    {
                        graphics.ReleaseHdc(hdc);
                    }

                    graphics.Dispose();
                }
            }
        }

        private static bool TryFindOfflineRewardCloseButton(Bitmap image, out Point clickPoint)
        {
            clickPoint = Point.Empty;
            int width = image.Width;
            int height = image.Height;
            if (width < 300 || height < 300)
            {
                return false;
            }

            int searchLeft = width / 4;
            int searchRight = width * 3 / 4;
            int searchTop = height / 4;
            int searchBottom = height * 4 / 5;
            int bestTitleY = -1;
            int bestTitleScore = 0;

            for (int y = searchTop; y < searchBottom; y += 2)
            {
                int score = 0;
                for (int x = searchLeft; x < searchRight; x += 2)
                {
                    if (IsOfflineRewardTitleRed(image.GetPixel(x, y)))
                    {
                        score++;
                    }
                }

                if (score > bestTitleScore)
                {
                    bestTitleScore = score;
                    bestTitleY = y;
                }
            }

            if (bestTitleY < 0 || bestTitleScore < Math.Max(20, (searchRight - searchLeft) / 25))
            {
                return false;
            }

            int titleBandTop = Math.Max(searchTop, bestTitleY - 18);
            int titleBandBottom = Math.Min(searchBottom, bestTitleY + 18);
            int yellowScore = 0;
            for (int y = titleBandTop; y <= titleBandBottom; y += 2)
            {
                for (int x = searchLeft; x < searchRight; x += 2)
                {
                    if (IsOfflineRewardTitleYellow(image.GetPixel(x, y)))
                    {
                        yellowScore++;
                    }
                }
            }

            if (yellowScore < 8)
            {
                return false;
            }

            int buttonSearchTop = Math.Min(height - 1, bestTitleY + height / 5);
            int buttonSearchBottom = Math.Min(height - 20, bestTitleY + height / 3 + height / 12);
            int buttonLeft = width / 3;
            int buttonRight = width * 2 / 3;
            int bestX = -1;
            int bestY = -1;
            int bestButtonScore = 0;
            int boxWidth = Math.Max(60, width / 12);
            int boxHeight = Math.Max(20, height / 30);

            for (int y = buttonSearchTop; y < buttonSearchBottom; y += 4)
            {
                for (int x = buttonLeft; x < buttonRight; x += 4)
                {
                    int score = CountButtonPixels(image, x, y, boxWidth, boxHeight);
                    if (score > bestButtonScore)
                    {
                        bestButtonScore = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestX < 0 || bestButtonScore < (boxWidth * boxHeight) / 10)
            {
                return false;
            }

            clickPoint = new Point(bestX + boxWidth / 2, bestY + boxHeight / 2);
            return true;
        }

        private static int CountButtonPixels(Bitmap image, int left, int top, int width, int height)
        {
            int right = Math.Min(image.Width, left + width);
            int bottom = Math.Min(image.Height, top + height);
            int score = 0;
            for (int y = top; y < bottom; y += 2)
            {
                for (int x = left; x < right; x += 2)
                {
                    if (IsOfflineRewardButtonBrown(image.GetPixel(x, y)))
                    {
                        score++;
                    }
                }
            }

            return score * 4;
        }

        private static bool IsOfflineRewardTitleRed(Color color)
        {
            return color.R >= 80 && color.R <= 180
                && color.G <= 70
                && color.B <= 70
                && color.R > color.G + 35
                && color.R > color.B + 35;
        }

        private static bool IsOfflineRewardTitleYellow(Color color)
        {
            return color.R >= 160
                && color.G >= 100
                && color.B <= 80
                && color.R >= color.G;
        }

        private static bool IsOfflineRewardButtonBrown(Color color)
        {
            return color.R >= 60 && color.R <= 150
                && color.G >= 20 && color.G <= 90
                && color.B <= 70
                && color.R > color.G + 15;
        }

        private static bool TryRealClick(IntPtr hWnd, int x, int y)
        {
            try
            {
                Rect rect;
                if (hWnd == IntPtr.Zero || !GetWindowRect(hWnd, out rect))
                {
                    return false;
                }

                PointStruct oldPosition;
                GetCursorPos(out oldPosition);

                TryBringWindowToFront(hWnd);
                Thread.Sleep(150);
                SetCursorPos(rect.Left + x, rect.Top + y);
                Thread.Sleep(40);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(40);
                SetCursorPos(oldPosition.X, oldPosition.Y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySendWindowToBack(IntPtr handle)
        {
            try
            {
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                return SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBringWindowToFront(IntPtr handle)
        {
            bool unusedOriginalWasTopMost;
            return TryBringWindowToFront(handle, false, out unusedOriginalWasTopMost);
        }

        private static bool TryBringWindowToFront(IntPtr handle, bool keepTopMost, out bool originalWasTopMost)
        {
            originalWasTopMost = false;
            try
            {
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                originalWasTopMost = IsWindowTopMost(handle);
                ShowWindow(handle, SwRestore);
                AllowSetForegroundWindow(-1);

                IntPtr foreground = GetForegroundWindow();
                uint currentThread = GetCurrentThreadId();
                int unusedProcessId;
                uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out unusedProcessId);
                uint targetThread = GetWindowThreadProcessId(handle, out unusedProcessId);

                bool attachedForeground = false;
                bool attachedTarget = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                    {
                        attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
                    }

                    if (targetThread != 0 && targetThread != currentThread)
                    {
                        attachedTarget = AttachThreadInput(currentThread, targetThread, true);
                    }

                    bool topMostSet = SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoOwnerZOrder);
                    if (!keepTopMost)
                    {
                        SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoOwnerZOrder);
                    }

                    BringWindowToTop(handle);
                    SetForegroundWindow(handle);
                    return keepTopMost ? topMostSet : true;
                }
                finally
                {
                    if (attachedTarget)
                    {
                        AttachThreadInput(currentThread, targetThread, false);
                    }

                    if (attachedForeground)
                    {
                        AttachThreadInput(currentThread, foregroundThread, false);
                    }
                }
            }
            catch
            {
                originalWasTopMost = false;
                return false;
            }
        }

        private static bool IsWindowTopMost(IntPtr handle)
        {
            return handle != IntPtr.Zero && (GetWindowLong(handle, GwlExStyle) & WsExTopMost) != 0;
        }

        private static Dictionary<string, bool> BuildProcessNameSet(string[] processNames)
        {
            var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string processName in processNames)
            {
                string normalized = Path.GetFileNameWithoutExtension((processName ?? "").Trim());
                if (normalized.Length > 0)
                {
                    set[normalized] = true;
                }
            }

            return set;
        }

        private static string[] SplitConfigList(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new string[0];
            }

            return value.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string GetWindowTitle(IntPtr handle)
        {
            try
            {
                int length = GetWindowTextLength(handle);
                if (length <= 0)
                {
                    return "";
                }

                var title = new StringBuilder(length + 1);
                GetWindowText(handle, title, title.Capacity);
                return title.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static string GetWindowClassName(IntPtr handle)
        {
            try
            {
                var className = new StringBuilder(256);
                GetClassName(handle, className, className.Capacity);
                return className.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static bool TitleMatches(string title, string[] titleTokens)
        {
            if (titleTokens == null || titleTokens.Length == 0)
            {
                return true;
            }

            foreach (string token in titleTokens)
            {
                string trimmed = (token ?? "").Trim();
                if (trimmed.Length > 0 && title.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateStartupMenuItem()
        {
            _startupItem.Checked = IsStartupRegistered();
        }

        private void ToggleStartupRegistration()
        {
            try
            {
                if (IsStartupRegistered())
                {
                    string shortcutPath = GetStartupShortcutPath();
                    if (File.Exists(shortcutPath))
                    {
                        File.Delete(shortcutPath);
                    }

                    _startupItem.Checked = false;
                    ShowBalloon("Startup registration removed", "Task Bar Hero Guard will not start with Windows.");
                    return;
                }

                CreateStartupShortcut();
                _startupItem.Checked = true;
                ShowBalloon("Startup registration added", "Task Bar Hero Guard will start with Windows.");
            }
            catch (Exception ex)
            {
                UpdateStartupMenuItem();
                ShowBalloon("Startup registration failed", ex.Message);
            }
        }

        private static bool IsStartupRegistered()
        {
            string shortcutPath = GetStartupShortcutPath();
            if (!File.Exists(shortcutPath))
            {
                return false;
            }

            string targetPath = ReadShortcutTargetPath(shortcutPath);
            return PathsEqual(targetPath, Application.ExecutablePath);
        }

        private static void CreateStartupShortcut()
        {
            string shortcutPath = GetStartupShortcutPath();
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath));

            object shell = null;
            object shortcut = null;
            try
            {
                shell = CreateWScriptShell();
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { AppDomain.CurrentDomain.BaseDirectory });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Task Bar Hero Guard" });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { Application.ExecutablePath });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static string ReadShortcutTargetPath(string shortcutPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                shell = CreateWScriptShell();
                shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                object targetPath = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null);
                return targetPath == null ? "" : targetPath.ToString();
            }
            catch
            {
                return "";
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static object CreateWScriptShell()
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell is not available.");
            }

            return Activator.CreateInstance(shellType);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        private static string GetStartupShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "TaskBarHeroGuard.lnk");
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left.Trim().Trim('"')), Path.GetFullPath(right.Trim().Trim('"')), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
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
            _thresholdItem.Text = "Limit: " + FormatBytes(_config.ThresholdBytes) + " / Hard: " + FormatBytes(_config.HardThresholdBytes);

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

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc enumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out PointStruct point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private sealed class WindowMatchState
        {
            public Dictionary<string, bool> ProcessNames;
            public string[] TitleTokens;
            public bool RequireTitleMatch;
            public bool ToBack;
            public bool KeepTopMost;
            public Dictionary<IntPtr, bool> TemporaryTopMostWindows;
            public HashSet<IntPtr> HandledWindows;
            public int Moved;
        }

        private sealed class WindowFindState
        {
            public int ProcessId;
            public string RequiredClassName;
            public IntPtr Handle;
        }

        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private struct PointStruct
        {
            public int X;
            public int Y;
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
        public long HardThresholdBytes = 1536L * 1024L * 1024L;
        public int CheckIntervalSeconds = 10;
        public int RestartDelaySeconds = 8;
        public int RestartCooldownSeconds = 120;
        public int GracefulCloseSeconds = 10;
        public bool AutoLaunchWhenMissing = false;
        public bool SendStartupWindowToBack = true;
        public int StartupWindowBackDurationSeconds = 30;
        public string StartupBackProcessNames = "steam.exe;steamwebhelper.exe";
        public string StartupBackWindowTitleContains = "Steam;Task Bar Hero;TaskbarHero;タスクバー ヒーロー;ゲームを起動中;起動中";
        public bool StartupBackRequireTitleMatch = false;
        public bool BringGameToFrontOnStageStart = true;
        public bool KeepGameTopMostAfterStageStart = true;
        public int StageStartFrontWaitSeconds = 300;
        public int StageStartFrontRetrySeconds = 8;
        public string StageStartLogSignalText1 = "TaskbarHero.StageManager:*(Boolean)";
        public string StageStartLogSignalText2 = "";
        public bool AutoCloseOfflineReward = true;
        public int AutoCloseOfflineRewardDelaySeconds = 8;
        public int AutoCloseOfflineRewardDurationSeconds = 45;
        public int AutoCloseOfflineRewardIntervalMilliseconds = 1000;
        public bool RestoreRestartWindowPosition = true;
        public int RestartWindowPositionTolerancePixels = 80;
        public int RestoreRestartWindowPositionSeconds = 60;
        public int RestoreRestartWindowPositionAfterStageStartSeconds = 15;
        public bool WaitForStageLog = true;
        public string GameDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).Replace("\\Local", "\\LocalLow"), "TesseractStudio", "TaskbarHero");
        public string PlayerLogPath = "";
        public string SaveFilePath = "";
        public string StageLogSignalText1 = "TaskbarHero.Log.LogManager:*(LogData)";
        public string StageLogSignalText2 = "TaskbarHero.UI_Stage:*(Int32)";
        public int MaxDeferralSeconds = 1800;
        public int StageSignalSettleSeconds = 5;
        public int SaveSettleSeconds = 5;
        public bool RequireSaveUpdate = true;
        public int MaxLogReadBytes = 1048576;

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
            KeyValuePair<string, string>[] defaults = DefaultConfigPairs();
            if (File.Exists(ConfigPath))
            {
                AppendMissingConfigKeys(defaults);
                return;
            }

            File.WriteAllText(ConfigPath, BuildDefaultConfig(defaults));
        }

        private static KeyValuePair<string, string>[] DefaultConfigPairs()
        {
            return new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>("ProcessName", "TaskBarHero.exe"),
                new KeyValuePair<string, string>("LaunchUri", "steam://rungameid/3678970"),
                new KeyValuePair<string, string>("ThresholdMB", "1024"),
                new KeyValuePair<string, string>("HardThresholdMB", "1536"),
                new KeyValuePair<string, string>("CheckIntervalSeconds", "10"),
                new KeyValuePair<string, string>("RestartDelaySeconds", "8"),
                new KeyValuePair<string, string>("RestartCooldownSeconds", "120"),
                new KeyValuePair<string, string>("GracefulCloseSeconds", "10"),
                new KeyValuePair<string, string>("AutoLaunchWhenMissing", "false"),
                new KeyValuePair<string, string>("SendStartupWindowToBack", "true"),
                new KeyValuePair<string, string>("StartupWindowBackDurationSeconds", "30"),
                new KeyValuePair<string, string>("StartupBackProcessNames", "steam.exe;steamwebhelper.exe"),
                new KeyValuePair<string, string>("StartupBackWindowTitleContains", "Steam;Task Bar Hero;TaskbarHero;タスクバー ヒーロー;ゲームを起動中;起動中"),
                new KeyValuePair<string, string>("StartupBackRequireTitleMatch", "false"),
                new KeyValuePair<string, string>("BringGameToFrontOnStageStart", "true"),
                new KeyValuePair<string, string>("KeepGameTopMostAfterStageStart", "true"),
                new KeyValuePair<string, string>("StageStartFrontWaitSeconds", "300"),
                new KeyValuePair<string, string>("StageStartFrontRetrySeconds", "8"),
                new KeyValuePair<string, string>("StageStartLogSignalText1", "TaskbarHero.StageManager:*(Boolean)"),
                new KeyValuePair<string, string>("StageStartLogSignalText2", ""),
                new KeyValuePair<string, string>("AutoCloseOfflineReward", "true"),
                new KeyValuePair<string, string>("AutoCloseOfflineRewardDelaySeconds", "8"),
                new KeyValuePair<string, string>("AutoCloseOfflineRewardDurationSeconds", "45"),
                new KeyValuePair<string, string>("AutoCloseOfflineRewardIntervalMilliseconds", "1000"),
                new KeyValuePair<string, string>("RestoreRestartWindowPosition", "true"),
                new KeyValuePair<string, string>("RestartWindowPositionTolerancePixels", "80"),
                new KeyValuePair<string, string>("RestoreRestartWindowPositionSeconds", "60"),
                new KeyValuePair<string, string>("RestoreRestartWindowPositionAfterStageStartSeconds", "15"),
                new KeyValuePair<string, string>("WaitForStageLog", "true"),
                new KeyValuePair<string, string>("GameDataPath", "%USERPROFILE%\\AppData\\LocalLow\\TesseractStudio\\TaskbarHero"),
                new KeyValuePair<string, string>("PlayerLogPath", ""),
                new KeyValuePair<string, string>("SaveFilePath", ""),
                new KeyValuePair<string, string>("StageLogSignalText1", "TaskbarHero.Log.LogManager:*(LogData)"),
                new KeyValuePair<string, string>("StageLogSignalText2", "TaskbarHero.UI_Stage:*(Int32)"),
                new KeyValuePair<string, string>("MaxDeferralSeconds", "1800"),
                new KeyValuePair<string, string>("StageSignalSettleSeconds", "5"),
                new KeyValuePair<string, string>("SaveSettleSeconds", "5"),
                new KeyValuePair<string, string>("RequireSaveUpdate", "true"),
                new KeyValuePair<string, string>("MaxLogReadBytes", "1048576")
            };
        }

        private static string BuildDefaultConfig(KeyValuePair<string, string>[] defaults)
        {
            var text = new System.Text.StringBuilder();
            text.Append("# Task Bar Hero Guard settings\r\n");
            foreach (KeyValuePair<string, string> pair in defaults)
            {
                text.Append(pair.Key).Append("=").Append(pair.Value).Append("\r\n");
            }

            return text.ToString();
        }

        private static void AppendMissingConfigKeys(KeyValuePair<string, string>[] defaults)
        {
            var existing = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in ReadPairs(ConfigPath))
            {
                existing[pair.Key] = true;
            }

            var missing = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, string> pair in defaults)
            {
                if (!existing.ContainsKey(pair.Key))
                {
                    missing.Append(pair.Key).Append("=").Append(pair.Value).Append("\r\n");
                }
            }

            if (missing.Length == 0)
            {
                return;
            }

            string prefix = File.ReadAllText(ConfigPath).EndsWith("\n") ? "" : "\r\n";
            File.AppendAllText(ConfigPath, prefix + "\r\n# Added by newer Task Bar Hero Guard\r\n" + missing);
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
            else if (normalizedKey == "hardthresholdmb")
            {
                HardThresholdBytes = ParseLong(value, 1536) * 1024L * 1024L;
            }
            else if (normalizedKey == "hardthresholdbytes")
            {
                HardThresholdBytes = ParseLong(value, HardThresholdBytes);
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
            else if (normalizedKey == "sendstartupwindowtoback")
            {
                SendStartupWindowToBack = ParseBool(value, SendStartupWindowToBack);
            }
            else if (normalizedKey == "startupwindowbackdurationseconds")
            {
                StartupWindowBackDurationSeconds = ParseInt(value, StartupWindowBackDurationSeconds);
            }
            else if (normalizedKey == "startupbackprocessnames")
            {
                StartupBackProcessNames = value;
            }
            else if (normalizedKey == "startupbackwindowtitlecontains")
            {
                StartupBackWindowTitleContains = value;
            }
            else if (normalizedKey == "startupbackrequiretitlematch")
            {
                StartupBackRequireTitleMatch = ParseBool(value, StartupBackRequireTitleMatch);
            }
            else if (normalizedKey == "bringgametofrontonstagestart")
            {
                BringGameToFrontOnStageStart = ParseBool(value, BringGameToFrontOnStageStart);
            }
            else if (normalizedKey == "keepgametopmostafterstagestart")
            {
                KeepGameTopMostAfterStageStart = ParseBool(value, KeepGameTopMostAfterStageStart);
            }
            else if (normalizedKey == "stagestartfrontwaitseconds")
            {
                StageStartFrontWaitSeconds = ParseInt(value, StageStartFrontWaitSeconds);
            }
            else if (normalizedKey == "stagestartfrontretryseconds")
            {
                StageStartFrontRetrySeconds = ParseInt(value, StageStartFrontRetrySeconds);
            }
            else if (normalizedKey == "stagestartlogsignaltext1")
            {
                StageStartLogSignalText1 = value;
            }
            else if (normalizedKey == "stagestartlogsignaltext2")
            {
                StageStartLogSignalText2 = value;
            }
            else if (normalizedKey == "autocloseofflinereward")
            {
                AutoCloseOfflineReward = ParseBool(value, AutoCloseOfflineReward);
            }
            else if (normalizedKey == "autocloseofflinerewarddelayseconds")
            {
                AutoCloseOfflineRewardDelaySeconds = ParseInt(value, AutoCloseOfflineRewardDelaySeconds);
            }
            else if (normalizedKey == "autocloseofflinerewarddurationseconds")
            {
                AutoCloseOfflineRewardDurationSeconds = ParseInt(value, AutoCloseOfflineRewardDurationSeconds);
            }
            else if (normalizedKey == "autocloseofflinerewardintervalmilliseconds")
            {
                AutoCloseOfflineRewardIntervalMilliseconds = ParseInt(value, AutoCloseOfflineRewardIntervalMilliseconds);
            }
            else if (normalizedKey == "restorerestartwindowposition")
            {
                RestoreRestartWindowPosition = ParseBool(value, RestoreRestartWindowPosition);
            }
            else if (normalizedKey == "restartwindowpositiontolerancepixels")
            {
                RestartWindowPositionTolerancePixels = ParseInt(value, RestartWindowPositionTolerancePixels);
            }
            else if (normalizedKey == "restorerestartwindowpositionseconds")
            {
                RestoreRestartWindowPositionSeconds = ParseInt(value, RestoreRestartWindowPositionSeconds);
            }
            else if (normalizedKey == "restorerestartwindowpositionafterstagestartseconds")
            {
                RestoreRestartWindowPositionAfterStageStartSeconds = ParseInt(value, RestoreRestartWindowPositionAfterStageStartSeconds);
            }
            else if (normalizedKey == "waitforstagelog")
            {
                WaitForStageLog = ParseBool(value, WaitForStageLog);
            }
            else if (normalizedKey == "gamedatapath")
            {
                GameDataPath = ExpandPath(value);
            }
            else if (normalizedKey == "playerlogpath")
            {
                PlayerLogPath = ExpandPath(value);
            }
            else if (normalizedKey == "savefilepath")
            {
                SaveFilePath = ExpandPath(value);
            }
            else if (normalizedKey == "stagelogsignaltext1")
            {
                StageLogSignalText1 = value;
            }
            else if (normalizedKey == "stagelogsignaltext2")
            {
                StageLogSignalText2 = value;
            }
            else if (normalizedKey == "maxdeferralseconds")
            {
                MaxDeferralSeconds = ParseInt(value, MaxDeferralSeconds);
            }
            else if (normalizedKey == "stagesignalsettleseconds")
            {
                StageSignalSettleSeconds = ParseInt(value, StageSignalSettleSeconds);
            }
            else if (normalizedKey == "savesettleseconds")
            {
                SaveSettleSeconds = ParseInt(value, SaveSettleSeconds);
            }
            else if (normalizedKey == "requiresaveupdate")
            {
                RequireSaveUpdate = ParseBool(value, RequireSaveUpdate);
            }
            else if (normalizedKey == "maxlogreadbytes")
            {
                MaxLogReadBytes = ParseInt(value, MaxLogReadBytes);
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

            // Game updates rename obfuscated StageManager methods. Migrate the old known exact
            // signature to a structural signal that keeps the class and Boolean call contract.
            if (StageStartLogSignalText1.Equals("TaskbarHero.StageManager:igs(Boolean)", StringComparison.Ordinal))
            {
                StageStartLogSignalText1 = "TaskbarHero.StageManager:*(Boolean)";
            }

            if (StageLogSignalText1.Equals("TaskbarHero.Log.LogManager:kil(LogData)", StringComparison.Ordinal))
            {
                StageLogSignalText1 = "TaskbarHero.Log.LogManager:*(LogData)";
            }

            if (StageLogSignalText2.Equals("TaskbarHero.StageManager:ihl(Int32)", StringComparison.Ordinal))
            {
                StageLogSignalText2 = "TaskbarHero.UI_Stage:*(Int32)";
            }

            if (ThresholdBytes <= 0)
            {
                ThresholdBytes = DefaultThresholdBytes;
            }

            if (HardThresholdBytes <= ThresholdBytes)
            {
                HardThresholdBytes = ThresholdBytes + 512L * 1024L * 1024L;
            }

            GameDataPath = ExpandPath(GameDataPath);
            if (string.IsNullOrWhiteSpace(PlayerLogPath))
            {
                PlayerLogPath = Path.Combine(GameDataPath, "Player.log");
            }

            if (string.IsNullOrWhiteSpace(SaveFilePath))
            {
                SaveFilePath = Path.Combine(GameDataPath, "SaveFile_Live.es3");
            }

            CheckIntervalSeconds = Clamp(CheckIntervalSeconds, 1, 3600);
            RestartDelaySeconds = Clamp(RestartDelaySeconds, 1, 300);
            RestartCooldownSeconds = Clamp(RestartCooldownSeconds, 10, 3600);
            GracefulCloseSeconds = Clamp(GracefulCloseSeconds, 0, 300);
            StartupWindowBackDurationSeconds = Clamp(StartupWindowBackDurationSeconds, 1, 300);
            StageStartFrontWaitSeconds = Clamp(StageStartFrontWaitSeconds, 1, 3600);
            StageStartFrontRetrySeconds = Clamp(StageStartFrontRetrySeconds, 1, 60);
            AutoCloseOfflineRewardDelaySeconds = Clamp(AutoCloseOfflineRewardDelaySeconds, 0, 300);
            AutoCloseOfflineRewardDurationSeconds = Clamp(AutoCloseOfflineRewardDurationSeconds, 1, 300);
            AutoCloseOfflineRewardIntervalMilliseconds = Clamp(AutoCloseOfflineRewardIntervalMilliseconds, 250, 10000);
            RestartWindowPositionTolerancePixels = Clamp(RestartWindowPositionTolerancePixels, 0, 2000);
            RestoreRestartWindowPositionSeconds = Clamp(RestoreRestartWindowPositionSeconds, 1, 300);
            RestoreRestartWindowPositionAfterStageStartSeconds = Clamp(RestoreRestartWindowPositionAfterStageStartSeconds, 0, 300);
            MaxDeferralSeconds = Clamp(MaxDeferralSeconds, 10, 86400);
            StageSignalSettleSeconds = Clamp(StageSignalSettleSeconds, 0, 3600);
            SaveSettleSeconds = Clamp(SaveSettleSeconds, 0, 3600);
            MaxLogReadBytes = Clamp(MaxLogReadBytes, 4096, 16 * 1024 * 1024);
        }

        private static string ExpandPath(string value)
        {
            return Environment.ExpandEnvironmentVariables(value ?? "").Trim();
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
