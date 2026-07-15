# Task Bar Hero Guard

Task Bar Hero Guard is a tiny Windows tray app for TBH: Task Bar Hero.
It watches `TaskBarHero.exe` memory usage and restarts the game through Steam when the configured limit is exceeded.
By default it waits for a Task Bar Hero stage-log signal before restarting, then confirms that the save file settled.

Steam AppID `3678970` is the public AppID for `TBH: Task Bar Hero`, so the default launch URI is:

```text
steam://rungameid/3678970
```

## Build

Run this from PowerShell:

```powershell
.\build.ps1
```

The executable is created at:

```text
dist\TaskBarHeroGuard.exe
```

The build uses the Windows .NET Framework C# compiler at:

```text
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

No .NET SDK or NuGet restore is required.

## Run

Start:

```powershell
.\dist\TaskBarHeroGuard.exe
```

The app stays in the Windows tray. Right-click the tray icon to:

- launch Task Bar Hero
- restart Task Bar Hero
- open config
- reload config
- exit

The current status and memory-limit rows are non-selectable informational labels rendered with the normal Windows menu text color.

## Config

On first run, `TaskBarHeroGuard.ini` is created next to the executable.

```ini
ProcessName=TaskBarHero.exe
LaunchUri=steam://rungameid/3678970
ThresholdMB=1024
HardThresholdMB=1536
CheckIntervalSeconds=10
RestartDelaySeconds=8
RestartCooldownSeconds=120
GracefulCloseSeconds=10
AutoLaunchWhenMissing=false
SendStartupWindowToBack=true
StartupWindowBackDurationSeconds=30
StartupBackProcessNames=steam.exe;steamwebhelper.exe
StartupBackWindowTitleContains=Steam;Task Bar Hero;TaskbarHero;タスクバー ヒーロー;ゲームを起動中;起動中
StartupBackRequireTitleMatch=false
BringGameToFrontOnStageStart=true
KeepGameTopMostAfterStageStart=true
StageStartFrontWaitSeconds=300
StageStartFrontRetrySeconds=8
StageStartLogSignalText1=TaskbarHero.StageManager:*(Boolean)
StageStartLogSignalText2=
AutoCloseOfflineReward=true
AutoCloseOfflineRewardDelaySeconds=8
AutoCloseOfflineRewardDurationSeconds=45
AutoCloseOfflineRewardIntervalMilliseconds=1000
RestoreRestartWindowPosition=true
RestartWindowPositionTolerancePixels=80
RestoreRestartWindowPositionSeconds=60
RestoreRestartWindowPositionAfterStageStartSeconds=15
WaitForStageLog=true
GameDataPath=%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero
PlayerLogPath=
SaveFilePath=
StageLogSignalText1=TaskbarHero.Log.LogManager:*(LogData)
StageLogSignalText2=TaskbarHero.UI_Stage:*(Int32)
MaxDeferralSeconds=1800
StageSignalSettleSeconds=5
SaveSettleSeconds=5
RequireSaveUpdate=true
MaxLogReadBytes=1048576
```

`ThresholdMB` is the restart threshold. The default is `1024` MB.
When `SendStartupWindowToBack=true`, the app sends the Task Bar Hero window and matching Steam launch windows behind other windows for `StartupWindowBackDurationSeconds` after launch.
When `StartupBackRequireTitleMatch=false`, Steam launch windows are matched by process name even if their title is blank or different.
When `BringGameToFrontOnStageStart=true`, the app watches the stage-start log signal and brings the Task Bar Hero window to the front.
When `KeepGameTopMostAfterStageStart=true`, the app keeps the main `UnityWndClass` game window topmost after a stage-start or manual tray bring-to-front action. The state is maintained for the rest of that game run, including if Unity replaces the window handle. Startup, splash, and Steam launch windows are not made topmost. If the game window was already topmost before the guard action, the guard preserves that original state when it exits or starts another restart.
When `AutoCloseOfflineReward=true`, the app captures the Task Bar Hero window during startup and performs a real mouse click only after the offline reward close button is visually detected. Startup Z-order handling is stopped before the click, the window is returned to the back once, and a fresh capture must confirm that the popup disappeared; otherwise detection and clicking are retried for the configured duration.

During startup, each Steam, splash, or Unity window handle is sent to the back at most once. This prevents the guard's timer from repeatedly fighting the game's own activation behavior while still handling replacement windows created later in the launch sequence.

Startup handling is initialized once per game launch. Steam launch request and later process discovery share the same handled-window set and log cursor, so process detection cannot send the final window back again or skip an already-written stage-start signal. `*` in a configured log signal matches characters within one log line, allowing the default stage signal to survive obfuscated method-name changes between game versions.
When `RestoreRestartWindowPosition=true`, the app remembers the game window position before a guard-triggered restart and keeps moving the relaunched window back when it starts far from that position until the restore window expires. When `BringGameToFrontOnStageStart=true`, the restore window remains available through the stage-start wait and restarts from the stage-start signal for at least `RestoreRestartWindowPositionSeconds`, so late Unity layout changes are corrected on the gameplay window.
When `WaitForStageLog=true`, crossing `ThresholdMB` only marks restart as pending.
The app restarts after it sees both stage-log signal strings in `Player.log` and `SaveFile_Live.es3` has been updated and settled.
`HardThresholdMB` bypasses the wait and restarts immediately.
`MaxDeferralSeconds` also forces a restart if no stage signal arrives in time.

The tray menu's `Restart Task Bar Hero` command creates a manual restart reservation instead of restarting immediately. Manual reservations wait without a hard-limit or maximum-deferral override until both stage completion and the settled save update are observed. Repeated menu clicks preserve the existing reservation and log cursor.

Command-line values override the config file:

```powershell
.\TaskBarHeroGuard.exe /ThresholdMB=1536 /CheckIntervalSeconds=5
```

## Restart Behavior

- Finds processes by `ProcessName`.
- Reads each process Working Set.
- Marks restart pending when the largest matching process reaches `ThresholdMB`.
- Restarts immediately when the largest matching process reaches `HardThresholdMB`.
- While pending, watches `Player.log` for the Task Bar Hero stage-log stack signal.
- Confirms `SaveFile_Live.es3` changed after the pending restart began.
- Tries `CloseMainWindow` first.
- Kills the process if graceful close times out.
- Launches Steam with `steam://rungameid/3678970`.
- Sends the game and Steam launch windows to the bottom of the Z order during startup when enabled.
- Brings the game window to the front when the configured stage-start log signal appears.
- Keeps the game window topmost after stage-start and manual tray bring-to-front actions when enabled.
- Toggles Windows startup registration from the tray menu by creating or removing a shortcut in the current user's Startup folder.
- Closes the offline reward popup during startup when the visual detector finds it.
- Restores the game window position after a restart until the restore window expires.
- Uses `RestartCooldownSeconds` to avoid restart loops.
