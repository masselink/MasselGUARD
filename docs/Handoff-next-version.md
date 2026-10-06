# Handoff - the version after 4.6.0

**Service back-end status (2026-10-05, branch `service-backend`, uncommitted):** design agreed in `docs/ServiceBackend-Design.md`; phase 0 (`IPrivilegedOps` seam) and phase 1a/1b (RPC, service host, installer, back-end selection, pipe hardening) are in the working tree, selftest 434 passed, both projects build. NOT yet tested on a real machine (needs an elevated `MasselGUARDcli service install`; test plan at the end of the design doc). Next: user test, then phase 1c (manifest `asInvoker` + direct-mode relaunch), `%ProgramData%` tunnel store, phase 2 (bypass/DNS state in the service), `/security-review`.

Status: **planning, nothing started.** Written at the end of the 4.6.0 work so a fresh session can pick up without the history. Read `CLAUDE.md` first (it is the codebase guide); this file adds the plan, the decisions already made and the traps.

Two goals:

1. **A configurable bypass shortcut** (small).
2. **Redo the back-end as a Windows service** (large, the real work).

The version number is **not decided**. Keep the code on 4.6.0 while working, as was done for 4.5.x -> 4.6.0, and choose the number at the end from what changed (the service rebuild is a candidate for 5.0.0 because it changes how the app is installed and run). Bump `UpdateChecker.cs` + `BUILD.bat` + both csproj files together at the end.

---

## 0. Where things stand

- **4.6.0 - Wired Weasel** is feature complete, documented (`docs/WHATSNEW.md`, release text in `docs/release-body-4.6.0.md`) and merged: local `main` was fast-forwarded to `dev` (`f5cab7c`). Whether it is pushed, tagged and published is the user's call; check `git status` / `git log origin/main..main`.
- Work continues on the **`dev`** branch.
- Selftest (`MasselGUARDcli selftest`): 335 checks, 10 suites. All 12 `lang/*.json` have the same key count (1218 at the end of 4.6.0).
- Design docs worth reading before starting: `docs/AutomationShortcuts-Design.md` (pause automation is still planned there, plus the open phase 2 of "create a rule from this network"), `docs/DnsAutomation-Design.md`, `docs/NetworkIdentity-Design.md`.

### Rules of engagement (from the user's standing instructions)

- **Never `git commit`** or push; the user commits. Never run the Debug `MasselGUARD.exe` (it self-installs into Program Files and breaks the installed copy). The user runs `BUILD.bat x64 nozip run` himself to test; verify with `dotnet build MasselGUARD.csproj` and `dotnet build MasselGUARDcli\MasselGUARDcli.csproj`, and `MasselGUARDcli selftest` from `MasselGUARDcli\bin\Debug\...`.
- The running GUI is **elevated**: it cannot be driven, killed or inspected from a non-elevated session (screen capture works, input does not).
- **No em-dashes** in any text (use a hyphen). **All 12 language files stay in key parity**; every user-facing string goes through `Lang.T`. Activity-log lines, the CLI and rule/DNS reasons stay English (see `CLAUDE.md`, Localization rules).
- Shared `Services/*.cs` that the CLI needs must be added to `MasselGUARDcli/MasselGUARDcli.csproj`'s explicit `<Compile Include>` list; `Models/*.cs` is globbed. Keep shared files WPF-free.
- Put pure logic in testable functions and add `RunSelfTest` cases (wire new suites into `Cli/CliRunner.cs` `CmdSelfTest`).
- Docs to keep in step: `CLAUDE.md`, `docs/MANUAL.md`, `docs/CLIManual.md`, `docs/WHATSNEW.md` (new top section for the next version), `docs/CliSelfTest.md`, the README.

### Traps found the hard way

- **Script encoding:** PowerShell 5.1 (`powershell.exe`, and bash heredocs piped to it) reads a BOM-less script as ANSI, so emoji and non-ASCII in the script come out as mojibake (`â±`). Write helper scripts with the Write tool and run them with the PowerShell tool (pwsh 7), or keep scripts ASCII and build characters with `[char]0x23F1`. After any scripted edit, grep for `â`/`Ã`.
- **`sed -i` on a `.bat`** converted CRLF to LF and broke `BUILD.bat` (cmd mis-parses labels). Never `sed -i` the batch files; if it happens, restore CRLF.
- **`Lang.T` has a `params` overload**, so a method group (`Lang.T`) does not convert to `Func<string,string>`; use a lambda.
- **Do not `using` a `StreamWriter` and `StreamReader` over the same pipe** (disposing the writer after the reader closed the pipe throws); use `leaveOpen`. The selftest has a real pipe round trip for this.
- A `GridSplitter` next to a zero-width column does nothing (see the column bullets in `CLAUDE.md`); every resize `Thumb` needs `Style="{StaticResource HandleThumb}"`.
- Menus opened with `FetchMenu.Show` need `atMouse: true` for right-click menus.

---

## 1. A configurable bypass shortcut - DONE on `dev` (2026-10-05)

Implemented as designed with these answers (the user had not answered the open questions, so the defaults were chosen): in-window shortcut from `config.json` (`BypassShortcut`, `BypassShortcutGlobal`), an optional system-wide hotkey, and a Settings recorder box. See the "Configurable shortcut" part of the timed-override bullet in `CLAUDE.md`, `Models/Shortcut.cs` and `Views/ShortcutKeys.cs`. Kept below for reference.

### Today
- The shortcut is hard-coded: **Ctrl+Shift+B** in `MainWindow.MainWindow_PreviewKeyDown` (`MainWindow.xaml.cs`, hooked in the constructor next to `InitColumnWidths`), plus **Shift+Enter** on the selected row in `DnsList_KeyDown`.
- It only works while the main window has focus. There is no global hotkey.
- Related pieces: `BypassDnsNow(int? seconds)` (toggle: start with the last length, press again to stop), `MainViewModel.UseDnsTemporarily`, `Models/TempOverride.cs`.

### Wanted
The shortcut set in the config (`config.json`), not hard-coded.

### Proposed design
- `AppConfig.BypassShortcut` (string, default `"Ctrl+Shift+B"`, empty = disabled). Parse with WPF's `KeyGestureConverter` at startup and when the setting changes; an unparsable value logs a warning and falls back to the default (never throws).
- Replace the hard-coded check with a comparison against the parsed `KeyGesture` (`gesture.Matches(null, e)` works for key events). Keep Shift+Enter as the fixed list shortcut.
- Optional second setting `BypassShortcutGlobal` (bool, default false): a system-wide hotkey through `RegisterHotKey` on the main window's `HwndSource` (the app is elevated, so it can register while elevated apps are focused). Needs: modifiers/key mapping from the gesture, `UnregisterHotKey` on change/exit, handling of "already registered by another app" (log + tell the user), and it must work while the window is hidden in the tray. Decide whether global is in scope or a follow-up.
- Settings UI (user asked for config; a UI is a small bonus): a read-only text box in **Settings > DNS** that captures a key press, plus a Clear button; show the current shortcut in the tray menu text and the footer tooltip so it is discoverable.
- Add the field to `PresetService.PolicyFields` (and the `dns` block) like `DnsTempOverrideEnabled`; add a preset label key.
- Tests: pure helper `ShortcutParser.TryParse(string, out Gesture)`-style function (modifier order, case, aliases like `Ctrl`/`Control`, invalid input) in a CLI-shared file with selftest cases; the WPF `KeyGesture` mapping stays in the GUI.

### Open questions for the user
1. Global hotkey, or in-window only?
2. Should the Explorer right-click entries and the tray text also show the shortcut?
3. A settings UI, or `config.json` only?

---

## 2. Redo the back-end as a Windows service

### Why
Today `MasselGUARD.exe` is `requireAdministrator` (UAC on every launch; the "Start with Windows" feature works around it with a Scheduled Task at highest run level). Everything privileged runs inside the UI process. A service split removes the UAC prompts, lets automation run with no window and no logged-on UI, fixes a class of OneDrive/LocalSystem problems, and gives the Explorer menu / browser extension / CLI a proper authenticated endpoint instead of the current "pipe into the elevated window" (`Services/CommandPipe.cs`).

### What needs administrator rights today (the service must take these over)
| Area | Code | Notes |
|---|---|---|
| Tunnel connect/disconnect (local) | `TunnelDll.cs`, `Services/TunnelService.cs` | `tunnel.dll` + `wireguard.dll`; the tunnel itself already runs as a Windows service `WireGuardTunnel$<name>` hosted by `MasselGUARD.exe /service <conf>` (`TunnelDll.HandleServiceArgs`, called first in `Program.Main`). |
| DNS overrides | `Services/DnsService.cs` | `netsh` per interface, DoH templates, snapshot in `%APPDATA%\MasselGUARD\dns_state.json`, restored on exit and after a crash. |
| Kill switch | `Services/KillSwitchService.cs` | Windows Firewall rules `MasselGUARD_KS_*`. |
| Pre/post tunnel scripts | `Services/ScriptService.cs` | Runs `powershell.exe` / `cmd.exe` **elevated today**. |
| Install / uninstall / autostart | `MainWindow.xaml.cs` `RunInstall` / `RunUninstall` (~line 4650-4740) | Program Files copy, HKLM uninstall key, Scheduled Task `MasselGUARD`. |
| Orphaned tunnel services, leak prevention | `GetOrphanedServices`, `DnsLeakService` | SCM and registry. |

What can stay unprivileged: the WPF UI, the rule dialogs, history/charts, themes, settings editing, network *reading* (`NetworkMonitor` uses `NetworkInterface` properties, `WiFiService` uses the WLAN API as a normal user), the CLI's read-only commands.

### Architecture options
- **A - thin privileged service (recommended first target).** The service executes a small, validated set of operations; the UI (now `asInvoker`) and the CLI call it. The rule engine and network watcher stay in the UI process.
- **B - service owns the automation too.** The service runs `NetworkWatcher`, `RuleEngine` and DnsPolicy and acts on its own, so automation works with no UI at all; the UI becomes a client and a settings editor. Bigger gain, bigger change (config ownership, history ownership, who writes `config.json`).
- Suggested path: **A first, designed so B is a later step.** The pure pieces (`RuleEngine`, `DnsPolicy`, `NetworkMatcher`, `RuleSimulator`, `TempOverride`) are already WPF-free and CLI-shared, so they can move into the service unchanged.

### Phasing
0. **Seam inside the current process.** Introduce an interface (name suggestion `IPrivilegedOps`: `ConnectTunnel`, `DisconnectTunnel`, `ApplyDns`, `RestoreDns`, `SetKillSwitch`, `RunScript`, status queries) and put `TunnelDll`/`TunnelService`, `DnsService`, `KillSwitchService` behind it with an in-process implementation. The app behaves exactly as today. This is the safe, reviewable refactor and the place to add selftests with a fake implementation.
1. **Service host + RPC.** A `MasselGUARDsvc` project (or a mode of the CLI exe) installed as a LocalSystem service; a named-pipe RPC server implementing `IPrivilegedOps`; the UI gets a proxy implementation. Switch the manifest to `asInvoker`. Installer registers/starts the service (one UAC prompt, at install). `CommandPipe` and the Explorer verb retarget the service.
2. **Move the bypass/DNS state into the service** (timer, `TempOverride`, `dns_state.json` recovery) so a bypass survives the UI closing.
3. **Optional B:** move the automation loop into the service.
4. Replace the Scheduled Task autostart with: service auto-start + a per-user `HKCU\...\Run` entry for the UI (no elevation).

### Design decisions that need an answer before coding
1. **DPAPI scope.** Tunnel configs are `*.conf.dpapi` encrypted with `DataProtectionScope.CurrentUser` (`ConfigService.cs:154`, `TunnelService.cs`); a LocalSystem service **cannot decrypt them**. Options: (a) the UI decrypts and sends the plaintext config over the authenticated pipe on connect (simple; secrets cross a local pipe), (b) re-encrypt tunnel storage with `LocalMachine` scope or a service-owned store under `%ProgramData%` with an ACL (also fixes the OneDrive "Element not found" problem, since the service would read from a local folder; needs a migration), (c) per-user storage the service reads by impersonating the caller. `TunnelService` already tries `LocalMachine` as a fallback for WireGuard-for-Windows configs (`TunnelService.cs:408`). Recommendation to discuss: (b) with a one-time migration, plus the user's own config staying in `%APPDATA%`.
2. **Pre/post scripts.** They run elevated today. In a service they would run as SYSTEM, which is a large escalation path for anything that can edit a tunnel's script field. Options: run scripts in the user's UI process (loses elevation), keep them elevated only through an explicit opt-in, or restrict them to files the service validates. Decide before moving `ScriptService`.
3. **Authorization of callers.** Who may call the service? Minimum: the interactive user who installed it (and Administrators), verified per connection (`GetNamedPipeClientProcessId` / impersonation to read the caller's token), with an operation allow-list and strict input validation (no arbitrary paths, no free-form commands). The current `CommandPipe` ACL (current user, SYSTEM, Administrators; bypass command only) is the starting point.
4. **Policy / managed presets.** The `.masselguard` lock is a soft lock today (user-writable files next to the exe). With a service the lock could become real (settings the service enforces, config under an ACL'd `%ProgramData%`). Decide how far to take it; at minimum keep `ConfigService.ApplyPreset` behaviour working.
5. **Config and history ownership** (matters for option B): per-user `%APPDATA%` today. A service serving several users needs a clear story (per-user config the service reads on request, or machine config).
6. **Updates.** `UpdateChecker.UpdateAsync` replaces files and restarts the app; the service must be stopped, its files replaced and restarted (the service exe is locked while running). Define the upgrade path and the rollback.
7. **Kill-switch and DNS crash recovery** move into the service's startup (restore stale firewall rules and `dns_state.json` on service start, like `RecoverDnsFromPreviousRun` does today).
8. **ARM64:** the service is another native exe per architecture; `BUILD.bat` and the packaging (`MasselGUARD-<arch>.zip`) need it, and the wireguard-NT driver constraint (native only on ARM64) is unchanged.
9. **Portable use must keep working (decided direction, details open).** Keep today's in-process implementation as a supported **direct mode** next to the service mode: the `IPrivilegedOps` seam from phase 0 has two implementations, and the app picks the service when it is installed and reachable, else direct mode (elevated, one UAC prompt, exactly like 4.6.0). Consequences to design: the exe manifest must become `asInvoker` (a manifest is per exe), so in direct mode the app **relaunches itself elevated** at startup (`runas`); "Install service" from a portable copy must **copy the app to a local folder** first (a LocalSystem service cannot read a cloud-synced folder such as OneDrive, and a service pointing at an unzipped folder breaks when the folder moves); Scoop installs (per-user, replaced on update) fit direct mode better than a service; a standard user needs the installed service. Test both modes on every phase, document the supported matrix, and keep the `AppRunMode` logic (`Managed`, `ManagedPortable`, not installed) in mind.
10. **Standard-user install story.** Decide the supported matrix: install needs admin once; after that a standard user can run the UI. Do portable (unzipped) runs still need an elevated mode?

### Risks
- It touches core, working code (connect/disconnect, auto-reconnect, kill switch, DNS ownership rules in `MainViewModel`), so regression risk is real; phase 0 and the selftests are the mitigation.
- The elevated GUI cannot be exercised from an assistant session, so every service milestone needs the user to test on a real machine; keep each phase small and shippable.
- Security review is mandatory for the pipe/RPC surface before release (`/security-review` on the branch).

### Test strategy
- Phase 0: fake `IPrivilegedOps` + selftest suite for the call sequences (connect -> DNS held while a tunnel owns it -> restore on the falling edge).
- Pure RPC message parsing/validation in a CLI-shared file with selftest cases (like `CommandPipe.ParseBypass`), including malformed and oversized input.
- A real pipe round trip on a private pipe name (pattern already in `CommandPipe.RunSelfTest`).
- Manual matrix for the user: fresh install, upgrade from 4.6.0, uninstall, standard user, two users, sleep/wake, service crash and restart, kill-switch cleanup after a crash.

---

## 3. Suggested order for the next session

1. Agree the version plan and answer the open questions (shortcut: 3 questions; service: the 10 decisions above, at least DPAPI scope, scripts and caller authorization).
2. Do **the shortcut** first as a warm-up (small, self-contained, shippable on its own), including the config field, preset field, language keys and selftest cases.
3. Write a design doc `docs/ServiceBackend-Design.md` from section 2 with the decisions recorded.
4. Phase 0 (the `IPrivilegedOps` seam), with selftests. Stop and let the user test.
5. Then phase 1 and onwards, one shippable step at a time.

## 4. Other open items to keep in view

- **Pause automation for N minutes** and phase 2 of **create a rule from this network** (toast button, tray item), both specified in `docs/AutomationShortcuts-Design.md`.
- Ideas not started: time/weekday conditions in network rules, a shadowed-rule warning, a Chromium extension for one-click link bypass (it would talk to the same endpoint, so it fits best after the service exists; Chrome's Secure DNS and DNS cache limit it), OpenVPN support (large; not planned).
- Release chores for 4.6.0 if not yet done: push `main`, tag `v4.6.0`, build both zips with a plain `BUILD.bat`, publish with `docs/release-body-4.6.0.md`, bump the Scoop bucket (version + both hashes), run `check-update` once.
