# WireGuard WiFi Switcher — C# / WPF

A native Windows companion app for WireGuard that automatically switches
VPN tunnels based on your current WiFi network.

---

## Build Instructions

### Requirements
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (free, for building)
- Windows 10/11 x64

### Build
1. Open this folder in a terminal (or double-click `BUILD.bat`)
2. Run:
   ```
   dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
   ```
3. Output: `dist\WGWifiSwitcher.exe`

---

## Runtime Requirements (target machine)

| Requirement | Notes |
|---|---|
| Windows 10/11 x64 | Required |
| [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | Free, ~50 MB. Pre-installed on many machines. |
| WireGuard | Official app installed, tunnels already imported |
| Administrator rights | Automatically requested on launch via UAC |

---

## Features

- **Auto-switching** — polls WiFi every 3 seconds, switches tunnel on SSID change
- **Rule manager** — map any WiFi SSID to any WireGuard tunnel
- **Default action** — do nothing / disconnect all / activate a fallback tunnel
- **System tray** — minimises to tray, double-click to restore
- **Activity log** — colour-coded timestamped log of every event
- **Frameless WPF UI** — dark theme, draggable window
- **Single .exe** — no installer needed; config saved to `%APPDATA%\WGWifiSwitcher\`

---

## Project Structure

```
WGWifiSwitcher/
├── BUILD.bat                  ← Double-click to build
├── WGWifiSwitcher.csproj
├── app.manifest               ← Requests UAC admin elevation
├── App.xaml / App.xaml.cs     ← App startup, tray icon
├── MainWindow.xaml            ← Main UI layout
├── MainWindow.xaml.cs         ← All app logic
└── Views/
    ├── RuleDialog.xaml        ← Add/Edit rule dialog
    └── RuleDialog.xaml.cs
```

---

## Auto-start at Login (optional)

Run once in an elevated PowerShell:

```powershell
$exe     = "C:\path\to\dist\WGWifiSwitcher.exe"
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$prin    = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
Register-ScheduledTask -TaskName "WGWifiSwitcher" `
    -Action $action -Trigger $trigger -Principal $prin
```

This launches the app at login with admin rights — no UAC prompt each time.
