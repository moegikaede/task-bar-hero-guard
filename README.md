# Task Bar Hero Guard

Task Bar Hero Guard is a tiny Windows tray app for TBH: Task Bar Hero.
It watches `TaskBarHero.exe` memory usage and restarts the game through Steam when the configured limit is exceeded.

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
CheckIntervalSeconds=10
RestartDelaySeconds=8
RestartCooldownSeconds=120
GracefulCloseSeconds=10
AutoLaunchWhenMissing=false
```

`ThresholdMB` is the restart threshold. The default is `1024` MB.

Command-line values override the config file:

```powershell
.\TaskBarHeroGuard.exe /ThresholdMB=1536 /CheckIntervalSeconds=5
```

## Restart Behavior

- Finds processes by `ProcessName`.
- Reads each process Working Set.
- Restarts when the largest matching process reaches `ThresholdMB`.
- Tries `CloseMainWindow` first.
- Kills the process if graceful close times out.
- Launches Steam with `steam://rungameid/3678970`.
- Uses `RestartCooldownSeconds` to avoid restart loops.
