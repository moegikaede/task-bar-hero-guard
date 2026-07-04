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
StageStartFrontWaitSeconds=300
StageStartFrontRetrySeconds=8
StageStartLogSignalText1=TaskbarHero.StageManager:igs(Boolean)
StageStartLogSignalText2=
AutoCloseOfflineReward=true
AutoCloseOfflineRewardDelaySeconds=8
AutoCloseOfflineRewardDurationSeconds=45
AutoCloseOfflineRewardIntervalMilliseconds=1000
WaitForStageLog=true
GameDataPath=%USERPROFILE%\AppData\LocalLow\TesseractStudio\TaskbarHero
PlayerLogPath=
SaveFilePath=
StageLogSignalText1=TaskbarHero.Log.LogManager:kil(LogData)
StageLogSignalText2=TaskbarHero.StageManager:ihl(Int32)
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
When `AutoCloseOfflineReward=true`, the app captures the Task Bar Hero window during startup and only performs a brief real mouse click on the close button after the offline reward popup is visually detected.
When `WaitForStageLog=true`, crossing `ThresholdMB` only marks restart as pending.
The app restarts after it sees both stage-log signal strings in `Player.log` and `SaveFile_Live.es3` has been updated and settled.
`HardThresholdMB` bypasses the wait and restarts immediately.
`MaxDeferralSeconds` also forces a restart if no stage signal arrives in time.

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
- Closes the offline reward popup during startup when the visual detector finds it.
- Uses `RestartCooldownSeconds` to avoid restart loops.
