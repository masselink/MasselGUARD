# MasselGUARD v2.2.1

**Automated WireGuard tunnel management for Windows**

MasselGUARD sits in the system tray and watches your WiFi. When you connect to a known network it activates the right WireGuard tunnel automatically. When you leave or connect to an unknown network a configurable default action fires — disconnect, activate a fallback tunnel, or do nothing. It also works as a manual client: connect and disconnect tunnels from the tunnel list or the tray menu without opening the WireGuard GUI.

<img width="1791" height="961" alt="Screenshot 2026-04-06 101218" src="https://github.com/user-attachments/assets/a3e4e238-c5eb-488e-bcbd-5cb4bd78103c" />


---

## Three operating modes

### 1 — Standalone

Manages WireGuard tunnels entirely without the official WireGuard application. Uses `tunnel.dll` and `wireguard.dll` (wireguard-NT) placed next to the executable.

- Create and edit tunnel configs inside the app
- Configs encrypted with DPAPI and stored as `.conf.dpapi` files
- A transient `WireGuardTunnel$<n>` Windows service is installed, started, and removed on each connect/disconnect

### 2 — WireGuard Companion

Works alongside the official WireGuard for Windows application. Automates connecting and disconnecting existing tunnels based on WiFi rules — no configs are stored or managed.

- Link tunnel profiles via **Import → Link to WireGuard profile**
- Unlink a profile at any time from the tunnel list
- WireGuard GUI and its live log accessible from the toolbar

### 3 — Mixed

Both modes active simultaneously. Manage standalone local tunnels and automate WireGuard-app tunnels side by side.

---

## Features

| Feature | Description |
|---|---|
| **Auto-switching** | Instant WiFi-triggered activation via `WlanRegisterNotification` — no polling |
| **WiFi rules** | Map any SSID to any tunnel; leave blank to disconnect on that network |
| **Default action** | Do nothing / disconnect all / activate a named fallback tunnel |
| **Open network protection** | Automatically activate a chosen tunnel on open (passwordless) WiFi, before any rule is evaluated |
| **Quick Connect** | Open any `.conf` or `.conf.dpapi` and connect instantly without importing; appears in the tunnel list and can be disconnected from there |
| **Tunnel list** | Live status; connect or disconnect with one click; Delete / Unlink / Remove button morphs based on selection |
| **System tray** | Coloured dot when connected; toggle tunnels from the tray menu |
| **Tray toast** | Branded popup near the tray when a tunnel switches while the app is hidden; shows reason (WiFi rule, default behaviour, open network protection) |
| **WireGuard log** | Live-tailing log window (Companion + Mixed, shown only when a WireGuard tunnel is active) |
| **Orphaned service cleanup** | Detect and remove `WireGuardTunnel$` SCM entries left behind after a crash; accessible in Settings → Advanced |
| **Setup wizard** | First-run wizard covering language, mode, and automation settings; re-runnable from Settings |
| **Multi-language** | 🇬🇧 English, 🇳🇱 Dutch, 🇩🇪 German, 🇫🇷 French, 🇪🇸 Spanish — add any language by dropping a JSON file in `lang\` |
| **Manual mode** | Disable WiFi-based auto-switching; control everything by hand |
| **Install / Uninstall** | Built-in installer: copies to Program Files, Start Menu shortcut, optional auto-start |
| **Update checker** | Compares running version against GitHub tags; shows update button when behind, witty message when ahead |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Rights | Administrator — UAC prompt on launch |
| **Standalone / Mixed** | `tunnel.dll` + `wireguard.dll` (wireguard-NT) next to the exe |
| **Companion / Mixed** | [WireGuard for Windows](https://wireguard.com/install) installed |

### Getting the DLLs (Standalone / Mixed)

| File | Source |
|---|---|
| `wireguard.dll` | wireguard-NT (~1.3 MB) from [download.wireguard.com/wireguard-nt](https://download.wireguard.com/wireguard-nt/). **Do not use** `wireguard.dll` from `C:\Program Files\WireGuard\` (~400 KB) — that version depends on `wireguard.sys` being pre-installed |
| `tunnel.dll` | Build from [wireguard-windows/embeddable-dll-service](https://github.com/WireGuard/wireguard-windows/tree/master/embeddable-dll-service) or download from the MasselGUARD repo |

Both files go next to `MasselGUARD.exe` in the `dist\` folder.

---

## How Standalone mode works

```
User clicks Connect
        │
        ▼
MainWindow.StartTunnel()
  ├─ Validate DLLs (wireguard-NT size check)
  ├─ DpapiDecrypt(.conf.dpapi) → plaintext
  ├─ WriteSecure(tunnels\temp\<n>.conf)
  │    └─ File.Create (empty) → SetAccessControl (SYSTEM+Admins+user)
  │       → StreamWriter (write plaintext)
  │
  ▼
TunnelDll.Connect()
  ├─ EnsureStopped (remove stale SCM entry)
  ├─ CreateService("WireGuardTunnel$<n>",
  │    binaryPath = "MasselGUARD.exe /service <conf>")
  ├─ ChangeServiceConfig2(SERVICE_SID_TYPE_UNRESTRICTED)
  └─ sc.Start()
        │
        ▼  (SCM spawns child process)
MasselGUARD.exe /service <conf>   ← runs as LocalSystem
  ├─ SetDllDirectory(exeDir)       ← so tunnel.dll finds wireguard.dll
  ├─ SetCurrentDirectory(exeDir)
  └─ WireGuardTunnelService(<conf>)
        │
        ▼
tunnel.dll installs wireguard-NT kernel driver,
brings tunnel up in kernel space, then exits (~50–100 ms).
Service process exits. Tunnel lives in kernel driver.

Back in MasselGUARD:
  └─ Poll SCM: Running or Stopped → success
  └─ Delete tunnels\temp\<n>.conf immediately
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
├── service-debug.log                written by the service child process
│
├── lang\
│   ├── en.json
│   ├── nl.json
│   ├── de.json
│   ├── fr.json
│   └── es.json
│
└── tunnels\                         local tunnel storage
    ├── home.conf.dpapi              DPAPI-encrypted, CurrentUser scope
    ├── office.conf.dpapi
    └── temp\                        transient plaintext copies for the service
            (empty when no tunnel is connecting)
```

**User config:** `%APPDATA%\MasselGUARD\config.json`

Key fields:

```json
{
  "Rules":               [{"Ssid": "HomeWifi", "Tunnel": "home"}],
  "DefaultAction":       "activate",
  "DefaultTunnel":       "home",
  "OpenWifiTunnel":      "home",
  "Mode":                "Standalone",
  "ManualMode":          false,
  "Language":            "en",
  "ShowTrayPopupOnSwitch": true
}
```

---

## Security

### DPAPI encryption

Local tunnel configs are stored as `.conf.dpapi` files encrypted with Windows **Data Protection API (DPAPI)** using `DataProtectionScope.CurrentUser`.

- Only the Windows user account that created the file can decrypt it
- The decryption key is derived from the user's login credentials by the OS
- Moving the file to another machine or user account makes it unreadable
- No passwords or keys are stored by the application

### Atomic temp file creation

When connecting, a plaintext copy of the config is written to `tunnels\temp\` so the `LocalSystem` service process can read it. The file is created with the correct ACL **from the first byte** — there is no window during which the file exists with inherited (looser) permissions:

```csharp
using (var empty = File.Create(path)) { }          // create empty, inherits parent ACL
new FileInfo(path).SetAccessControl(fileSec);       // lock down before writing
using var sw = new StreamWriter(new FileStream(...)); // write plaintext
```

ACL on the temp file: SYSTEM + Administrators + owning user only.

### Temp file lifetime

The plaintext temp file is deleted **immediately after `TunnelDll.Connect` returns** — once the service child process has called `WireGuardTunnelService()` the kernel driver has parsed the config and the file is no longer needed. Typical lifetime: under 200 ms. `StopTunnel` makes a second best-effort delete as a safety net.

### Service name sanitisation

`SafeName()` replaces spaces, backslashes, and all `Path.GetInvalidFileNameChars()` with underscores before using a tunnel name as an SCM service name or filename. The display name (with spaces) is preserved in `config.json` and the UI.

### ACL summary

| Location | ACL |
|---|---|
| `tunnels\` directory | SYSTEM + Administrators: Full Control (inherited); Authenticated Users: list/traverse only (not inherited onto files) |
| `tunnels\<n>.conf.dpapi` | SYSTEM + Administrators + owning user: Full Control; protected (no inheritance) |
| `tunnels\temp\` directory | SYSTEM + Administrators + current user: Full Control; protected |
| `tunnels\temp\<n>.conf` | Created with `FileSecurity`: SYSTEM + Administrators + current user only; deleted within ~200 ms |

---

## Quick Connect

Quick Connect opens a `.conf` or `.conf.dpapi` file from anywhere on disk and connects immediately — no import, no permanent storage.

1. Click **⚡ Quick Connect** in the status bar
2. Pick a `.conf` or `.conf.dpapi` file
3. The tunnel activates; a `⚡ <name>` entry appears at the top of the tunnel list
4. Click either the status-bar button or the tunnel list entry to disconnect

The config is decrypted if necessary, written securely to `tunnels\temp\`, and deleted immediately after the service starts.

---

## Open network protection

When the device connects to a WiFi network with no password (open/unsecured), MasselGUARD detects this via `WLAN_SECURITY_ATTRIBUTES.bSecurityEnabled` (offset 580 in `WLAN_CONNECTION_ATTRIBUTES`) and activates the configured protection tunnel **before** any SSID rule or default action is evaluated.

Configure the tunnel in the **Default Action** section of the main window, or leave it set to "— none —" to disable the feature.

---

## Orphaned service cleanup

`WireGuardTunnel$` services can be left in the Windows SCM after a crash or forced process termination (`OnExit` never runs, so `DisconnectAll()` is not called). These services:

- Consume a small amount of SCM resources
- Can prevent a tunnel from reconnecting if the same service name is already registered

MasselGUARD detects orphans by enumerating `WireGuardTunnel$` entries in the SCM that are not in `config.json`, and checks whether a WireGuard network adapter with the same name still exists (indicating the kernel tunnel is still live).

Cleanup is available in **Settings → Advanced → Possible Orphaned Tunnel Services**. A warning is also logged at startup if orphans are found.

---

## Build

```bat
BUILD.bat
```

The script compiles first and only asks about DLLs after a successful build.

Manual:

```bat
dotnet publish MasselGUARD.csproj -c Release -r win-x64 --self-contained false -o dist
```

Copy `tunnel.dll` and `wireguard.dll` into `dist\` to enable Standalone mode.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| "Cannot find file" in Event Viewer | Wrong `wireguard.dll` (WireGuard-app version, not wireguard-NT) | Use the ~1.3 MB wireguard-NT DLL from download.wireguard.com/wireguard-nt |
| Event Viewer termination error even when tunnel works | wireguard-NT service exits fast after tunnel is up — SCM logs a false positive | Ignore; check the tunnel list for green status |
| Config file not found on connect | Tunnel created with an older version; migration runs automatically on first connect | If migration fails, delete and re-add the tunnel |
| Quick Connect fails | DLLs missing, or file is not valid WireGuard syntax | Check DLL status in Settings; validate the `.conf` file |
| Orphaned services at startup | App was killed while a tunnel was active | Use Settings → Advanced → Possible Orphaned Tunnel Services to remove them |
