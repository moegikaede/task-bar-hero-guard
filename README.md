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

- check status
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
- Uses `RestartCooldownSeconds` to avoid restart loops.
