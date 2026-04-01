# MasselGUARD v2.1

**Automated WireGuard tunnel management for Windows**  
by [Harold Masselink](https://github.com/masselink/MasselGUARD)

---

## What it does

MasselGUARD sits in the system tray and watches your WiFi. When you connect to a known network it activates the right WireGuard tunnel automatically. When you leave or connect to an unknown network a configurable default action fires — disconnect, activate a fallback tunnel, or do nothing.

It also works as a manual client: connect and disconnect tunnels from the tunnel list or the tray menu without opening the WireGuard GUI.

---

## Three operating modes

Choose your mode in **Settings → App Mode**.

### Mode 1 — Standalone

Manages WireGuard tunnels entirely on its own using `tunnel.dll` and `wireguard.dll` placed next to the executable. The official WireGuard for Windows application is not required.

- Create and edit tunnel configs directly inside the app
- Configs are stored as DPAPI-encrypted `.conf.dpapi` files (see [Security](#security))
- A transient Windows service (`WireGuardTunnel$<name>`) is installed, started, and removed automatically on each connect/disconnect

### Mode 2 — WireGuard Companion

Works alongside the official WireGuard for Windows app. MasselGUARD automates connecting and disconnecting existing WireGuard tunnels based on WiFi rules — it does not store or manage configs itself.

- Link tunnel profiles from the WireGuard installation via **Import → Link to WireGuard profile**
- Unlink a profile at any time from the tunnel list without affecting the WireGuard app
- The WireGuard GUI and its live log are accessible directly from MasselGUARD's toolbar

### Mode 3 — Mixed (default)

Both modes active simultaneously. Manage standalone local tunnels and automate WireGuard-app tunnels side by side.

---

## Features

| Feature | Description |
|---|---|
| **Auto-switching** | Instant WiFi-triggered tunnel activation via `WlanRegisterNotification` (no polling) |
| **Rules** | Map any SSID to any tunnel; leave blank to disconnect on that network |
| **Default action** | Do nothing / disconnect all / activate a named fallback tunnel |
| **Tunnel list** | Live status for every tunnel; connect or disconnect with one click |
| **Quick Connect** | Open any `.conf` or `.conf.dpapi` and connect instantly — no import needed; appears in the active tunnel list and can be disconnected from there |
| **System tray** | Coloured dot when connected; toggle tunnels from the tray menu |
| **WireGuard log** | Live-tailing log window (Companion + Mixed, shown only when a WireGuard tunnel is active) |
| **Multi-language** | English and Dutch; add any language by dropping a JSON file in `lang\` |
| **Manual mode** | Disable WiFi-based auto-switching and control everything by hand |
| **Install / Uninstall** | Built-in installer — copies to Program Files, Start Menu shortcut, optional auto-start |
| **Update checker** | Compares running version against GitHub tags; shows update button when behind, a proud message when ahead |
| **Dark theme** | Frameless WPF, Consolas font, fully themed |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Rights | Administrator — UAC prompt on launch |
| **Standalone / Mixed** | `tunnel.dll` + `wireguard.dll` next to the exe |
| **Companion / Mixed** | WireGuard for Windows installed |

### Getting the DLLs

Run `get-wireguard-dlls.ps1` or place them manually:

| File | Source |
|---|---|
| `wireguard.dll` | **wireguard-NT** (~1.3 MB) — download from [download.wireguard.com/wireguard-nt](https://download.wireguard.com/wireguard-nt/). **Do not use** the `wireguard.dll` from `C:\Program Files\WireGuard\` — that version (~400 KB) relies on `wireguard.sys` already installed by the WireGuard app and will fail with "cannot find file" when used standalone |
| `tunnel.dll` | Build from [wireguard-windows/embeddable-dll-service](https://github.com/WireGuard/wireguard-windows/tree/master/embeddable-dll-service) |

---

## How Standalone mode works

```
User clicks Connect
        │
        ▼
MainWindow.StartTunnel()
  ├─ Validates DLLs (wireguard-NT size check)
  ├─ DpapiDecrypt(.conf.dpapi) → plaintext
  ├─ WriteSecure(tunnels\temp\<name>.conf)   ← atomic, locked ACL from byte 0
  │
  ▼
TunnelDll.Connect()
  ├─ EnsureStopped (removes any stale SCM entry)
  ├─ CreateService("WireGuardTunnel$<name>",
  │    binaryPath = "MasselGUARD.exe /service <conf>")
  ├─ ChangeServiceConfig2(SERVICE_SID_TYPE_UNRESTRICTED)
  └─ sc.Start()
        │
        ▼ (SCM spawns child process)
MasselGUARD.exe /service <conf>   ← runs as LocalSystem
  ├─ SetDllDirectory(exeDir)      ← so tunnel.dll finds wireguard.dll
  ├─ SetCurrentDirectory(exeDir)
  └─ WireGuardTunnelService(<conf>)
        │
        ▼
tunnel.dll installs wireguard-NT kernel driver,
brings up the tunnel in kernel space, then exits.
Service process exits (~50–100 ms after start).
Tunnel stays active in the kernel driver.

Back in MasselGUARD:
  └─ Polls SCM: Running or Stopped → success
  └─ Deletes tunnels\temp\<name>.conf immediately
```

On **disconnect**: `TunnelDll.Disconnect()` stops and removes the SCM entry. The kernel driver tears down the tunnel.

---

## File and directory layout

```
<ExeDir>\                            ExeDir = exe location (Program Files when installed)
│
├── MasselGUARD.exe
├── tunnel.dll                       wireguard-windows embeddable-dll-service
├── wireguard.dll                    wireguard-NT (embeds kernel driver)
├── service-debug.log                written by the service child process (diagnostics)
│
├── lang\
│   ├── en.json
│   └── nl.json
│
└── tunnels\                         local tunnel storage
    ├── home.conf.dpapi              DPAPI-encrypted, CurrentUser scope
    ├── office.conf.dpapi
    └── temp\                        transient plaintext copies for the service
            (empty when no tunnel is connecting)
```

**User config:** `%APPDATA%\MasselGUARD\config.json`

```json
{
  "Rules": [
    { "Ssid": "HomeWifi",  "Tunnel": "home"   },
    { "Ssid": "CafeWifi",  "Tunnel": ""        }
  ],
  "DefaultAction": "activate",
  "DefaultTunnel": "home",
  "Mode": "Standalone",
  "ManualMode": false,
  "Language": "en",
  "LogLevelSetting": "normal",
  "SuppressPortableUpdatePrompt": false,
  "InstalledPath": null
}
```

---

## Security

### DPAPI encryption

Local tunnel configs are stored as `.conf.dpapi` files encrypted with Windows **Data Protection API (DPAPI)** using `DataProtectionScope.CurrentUser`. This means:

- Only the Windows user account that created the file can decrypt it
- The decryption key is derived from the user's login credentials by the OS
- Moving the file to another machine or user account makes it unreadable
- No passwords or keys are stored in the application or in `config.json`

### Atomic temp file creation

When connecting, a plaintext copy of the config is needed so the `LocalSystem` service process can read it. This copy is created using `FileStream` with a `FileSecurity` object passed at open time:

```csharp
var fileSec = new FileSecurity();
// SYSTEM + Administrators + current user only
fileSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
// ... add ACL entries ...
new FileStream(path, FileMode.Create, FileSystemRights.WriteData,
    FileShare.None, 4096, FileOptions.None, fileSec);
```

The file is created with the correct ACL **from the first byte**. There is no window where the file exists with looser inherited permissions.

### Temp file lifetime

The plaintext temp file is deleted **immediately after `TunnelDll.Connect` returns** — once the service child process has called `WireGuardTunnelService()` the kernel driver has already parsed the config and the file is no longer needed. The typical lifetime is under 200 ms.

As a safety net, `StopTunnel` also attempts to delete any lingering temp file when the tunnel is disconnected.

### ACL summary

| Location | ACL |
|---|---|
| `tunnels\` directory | SYSTEM + Administrators: Full Control (inherited); Authenticated Users: list/traverse only (not inherited onto files) |
| `tunnels\<n>.conf.dpapi` | SYSTEM + Administrators + owning user: Full Control; protected (no inheritance) |
| `tunnels\temp\` directory | SYSTEM + Administrators + current user: Full Control; protected |
| `tunnels\temp\<n>.conf` | Created with FileSecurity: SYSTEM + Administrators + current user only; deleted within ~200 ms |

### Service name sanitisation

Windows service names and SCM binary path arguments do not allow spaces or backslashes. MasselGUARD applies `SafeName()` to every tunnel name before using it as a service name or filename:

- Spaces → `_`
- Backslashes → `_`
- Any other `Path.GetInvalidFileNameChars()` character → `_`

The original display name (with spaces) is preserved in `config.json` and shown in the UI.

---

## Quick Connect

Quick Connect opens a `.conf` or `.conf.dpapi` file from anywhere on disk and connects immediately — no import, no storage in the tunnel list.

1. Click **⚡ Quick Connect** in the status bar
2. Pick a `.conf` or `.conf.dpapi` file
3. The tunnel activates; the button label changes to **⚡ `<name>` ✕**
4. A synthetic entry **⚡ `<name>`** appears at the top of the tunnel list with an active indicator
5. Click the button again **or** click Disconnect on the tunnel list entry to disconnect

The config is read from the original location, decrypted if necessary, written securely to `tunnels\temp\`, and deleted immediately after the service starts. Nothing is stored permanently.

---

## Build

```bat
BUILD.bat
```

The script compiles first and only asks about DLLs (`tunnel.dll` + `wireguard.dll`) after a successful build, so you don't wait for downloads if the code has errors.

Manual:

```bat
dotnet publish MasselGUARD.csproj -c Release -r win-x64 --self-contained false -o dist
```

Copy `tunnel.dll` and `wireguard.dll` into `dist\` to enable Standalone mode.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| "Cannot find file" in Event Viewer | Wrong `wireguard.dll` (WireGuard-app version instead of wireguard-NT) | Use the ~1.3 MB wireguard-NT dll from download.wireguard.com/wireguard-nt/ |
| Tunnel connects then Event Viewer shows termination error | Normal — wireguard-NT service exits after tunnel is up in kernel; the event is a false positive from the SCM | Ignore; check the tunnel list for green status |
| Config file not found on connect | Tunnel was created with an older version; migration runs automatically on first connect | If migration fails, delete the tunnel and re-add it |
| Quick Connect fails | DLLs missing, or file is not valid WireGuard syntax | Check DLL status in Settings; validate the `.conf` file |

---

## License

MIT — see [LICENSE](LICENSE) for details.
