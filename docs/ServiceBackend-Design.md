# Service back-end - design

Branch: `service-backend`. Status: design agreed (recommendations accepted 2026-10-05), phase 0 starting. Background and the list of privileged areas: `docs/Handoff-next-version.md` section 2.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | DPAPI scope | Tunnel configs move to a service-owned store under `%ProgramData%\MasselGUARD\tunnels\` (ACL: SYSTEM + Administrators full, installing user read/write via the service only), encrypted with `LocalMachine` scope. One-time migration from `%APPDATA%\...\tunnels\*.conf.dpapi` (CurrentUser) done by the UI, which can still decrypt them. Direct mode keeps the per-user store. The user's `config.json` stays in `%APPDATA%`. |
| 2 | Scripts | Pre/post tunnel scripts run in the **user's UI process** by default (not elevated, never SYSTEM). Elevated execution is an explicit per-user opt-in, and only in direct mode. The service never runs free-form commands. |
| 3 | Caller authorization | Named pipe ACL: SYSTEM, Administrators, the installing user. Per connection the service checks the client token (impersonation / `GetNamedPipeClientProcessId`). Operation allow-list, strict validation of every argument (tunnel names against the store, no paths, no commands). Start from the `CommandPipe` ACL. |
| 4 | Presets | Keep `ConfigService.ApplyPreset` as is (soft lock). A hard lock via ACL'd `%ProgramData%` is a later option, not part of this work. |
| 5 | Ownership | Option A first: rule engine, network watcher, history and config stay in the UI process. The service executes privileged operations only. Pure pieces (`RuleEngine`, `DnsPolicy`, `NetworkMatcher`, `TempOverride`) stay WPF-free so option B can move them later. |
| 6 | Architecture | **A (thin privileged service)**, designed so B is a later step. |
| 9 | Portable | **Direct mode stays supported.** `IPrivilegedOps` has two implementations: in-process (today's code) and a pipe proxy. The app uses the service when installed and reachable, else direct mode (relaunches itself elevated, as 4.6.0 does). Installing the service from a portable copy first copies the app to a local folder. |
| 10 | Standard user | Installing needs admin once. After that a standard user runs the UI against the service. Portable runs need direct mode (elevated). |

Still open (decide in the phase that needs them): updates path (6), crash recovery placement (7, goes into service start-up), ARM64 packaging (8).

## The seam: `IPrivilegedOps`

The operations that need administrator rights, as one interface. Arguments are plain data so they can cross a pipe later.

- Tunnels: `Connect`, `Disconnect`, `IsRunning` / status.
- DNS: `ApplyDns`, `RestoreDns`, `RecoverDns`.
- Kill switch: `EnableKillSwitch`, `DisableKillSwitch`.
- Scripts: not in the interface (run in the user process, decision 2).

Implementations:
- `InProcessOps` - delegates to the existing static services. Behaviour identical to 4.6.0.
- `FakeOps` (selftest only) - records calls so call-sequence rules can be tested.
- `PipeOps` (phase 1) - RPC proxy to the service.

## Phases

0. Seam in the current process, behaviour unchanged, selftests with `FakeOps`.
1. Service host + RPC, manifest `asInvoker`, installer registers the service.
2. Bypass/DNS state moves into the service (survives the UI closing).
3. Optional: automation loop in the service (option B).
4. Autostart: service auto-start + per-user `HKCU\...\Run` for the UI.

Each phase ends with a stop for the user to test on a real machine. `/security-review` is mandatory on the pipe/RPC surface before release.

## Phase 1 status (2026-10-05)

Built (untested on a real machine, the service needs an elevated install):
- `Services/PrivilegedRpc.cs` (shared): JSON-line RPC. `RpcValidator` (every argument: tunnel names, configs without PreUp/PostUp/PreDown/PostDown, interface ids, address families, endpoint, bypass CIDRs, DNS profiles), `RpcDispatcher` (allow-list of 12 ops), `RpcServer` (pipe `MasselGUARD.Service`, per-connection check via `RunAsClient`: Administrators or a SID in `%ProgramData%\MasselGUARD\service-users.txt`; 128 KB request cap, 10 s read/write timeouts), `RpcOps` (client proxy implementing `ITunnelOps`/`IKillSwitchOps`/`IDnsOps`; status reads stay local). 54 selftest checks incl. real pipe round trips with an authorizer that says yes and one that says no.
- `Services/ServiceHost.cs` (GUI exe, `MasselGUARD.exe /svc`, LocalSystem): starts the server over the real implementations, recovers stale kill-switch rules and DNS overrides at start, restores DNS and the firewall on stop, stages configs in `%ProgramData%\MasselGUARD\run\<guid>\<name>.conf` (SYSTEM + Administrators only) and deletes them after the connect.
- `Services/ServiceInstaller.cs` (shared) + CLI `service install [--user name] | uninstall | status`: sc.exe, auto-start, restart on failure, refuses cloud-synced or network paths, records the installing user.
- `Services/PrivilegedBackend.cs` (GUI): at startup picks the service when it is installed and answering, else direct mode. `MainWindow`/`MainViewModel`/`TunnelService` get their ops from it; app exit disconnects tunnels by name in service mode.

Deliberately NOT done yet (phase 1c, needs a test cycle first):
- `app.manifest` is still `requireAdministrator`. Flipping it to `asInvoker` needs the direct-mode self-relaunch with `runas` in `Program.Main`, and a pass over everything that assumes elevation (install/uninstall, the Scheduled Task autostart).
- Tunnel configs still live in the per-user DPAPI store and cross the pipe as plaintext (decision 1 interim option); the `%ProgramData%` store with a migration is a later step.
- `CommandPipe` / the Explorer verb still talk to the running window, not the service.
- `DnsService` state is still `%APPDATA%\MasselGUARD\dns_state.json` (the SYSTEM profile when run as the service).
- Pre/post scripts still run in the UI process through `ScriptService` (decision 2 already holds in service mode).

Manual test plan for the user (elevated terminal, from `dist\x64`):
1. `MasselGUARDcli service status` -> not installed.
2. `MasselGUARDcli service install`, then `service status` -> Running, pipe answering.
3. Start `MasselGUARD.exe`: activity log (Debug level) shows `Back-end: MasselGUARD service`.
4. Connect/disconnect a tunnel, with kill switch on, then a DNS profile; check `sc query WireGuardTunnel$<name>`, `netsh advfirewall firewall show rule name=all | findstr MasselGUARD_KS`, `netsh interface ip show dns`.
5. Close the app: DNS restored, tunnel stopped. Kill `MasselGUARD.exe` from Task Manager mid-session, restart the service: stale rules/DNS cleaned (see `%ProgramData%\MasselGUARD\service.log`).
6. `service uninstall`, start the app again: direct mode as before.

### Easy install (phase 1d, 2026-10-06)
- `ServiceInstaller.InstallAuto/UninstallAuto`: direct when the process is elevated, otherwise `MasselGUARDcli.exe service install --user "DOMAIN\name"` through a `runas` verb (one UAC prompt; the caller, not the admin account, is recorded as allowed).
- Settings > Startup > Background service card (status line + Install/Remove button), the same card in wizard step 3, and a Yes/No offer at the end of the normal install (`DoInstall`). The button on a portable copy installs MasselGUARD first (`_installServiceAfterInstall` skips the second question). `DoInstall` stops the service before replacing the exe and re-registers it afterwards; `RunUninstall` removes it first. Strings: 15 keys in all 12 language files.

### Phase 2 (2026-10-06): timed DNS override held by the service
- `Services/DnsHoldKeeper.cs` (shared, pure, 13 selftest checks): one hold = profile id + interfaces + absolute UTC end. The service ticks it every second and, 3 s after the end (grace, so a running window's own end-of-override that re-applies automation normally wins), restores the held interfaces. New RPC ops `dns.hold`, `dns.hold.cancel`, `dns.hold.status`, `dns.release` (restore every override except held interfaces); the client side is `IDnsHoldOps` (implemented by `RpcOps`, `PrivilegedBackend.Hold`, null in direct mode).
- `MainViewModel`: `UseDnsTemporarily` registers the hold; every manual DNS action ends it (`ClearDnsTemp`); window exit calls `ReleaseExceptHeld` instead of `RestoreAll`; startup (`RecoverDnsFromPreviousRun`) adopts a still-running hold (manual profile + countdown) instead of wiping it.
- `DnsService` takes a state-file path; the service stores `dns_state.json` under `%ProgramData%\MasselGUARD` (SYSTEM has no useful %APPDATA%).
- Not done: a hold does not survive a service restart or reboot (DNS is restored at service start); the rule-engine/automation DNS still lives in the window (option B, phase 3).
- Test plan: service mode, right-click a DNS profile > 5 min (or Ctrl+Shift+B), then (1) close MasselGUARD from the tray: `netsh interface ip show dns` still shows the bypass resolver; (2) reopen within the time: the footer countdown continues; (3) close and wait: DNS is restored about 3 s after the end; (4) click Stop now while running: DNS returns to automation at once and nothing is restored again later; (5) direct mode (stop the service): closing the window restores DNS immediately, as before.

### Phase 1c (2026-10-06): manifest asInvoker
- `app.manifest` is `asInvoker`. `Program.Main`: elevated -> continue; service running + pipe answers (server PID verified) -> unelevated; else scheduled task, else UAC relaunch. See the CLAUDE.md bullet for the list of actions guarded by `EnsureElevated()`.
- Test plan: (1) service running, launch from a normal (non-admin) Explorer double-click: no UAC, footer/diagnostics show "Not elevated - privileged operations go through the MasselGUARD service", connect/disconnect, kill switch, DNS all work. (2) Stop the service (`sc stop MasselGUARDsvc`), launch: UAC prompt, direct mode as 4.6.0. (3) Service mode, Settings > Startup > Start with Windows: asks to restart elevated. (4) Launch a second copy while the first runs (elevated and unelevated mixes): the existing instance is brought to front. (5) Update with the service installed: asks for elevation, the service is restarted afterwards.
- Known gap: with the service the scheduled-task autostart still starts the app elevated (RunLevel Highest); phase 4 replaces it by a per-user Run entry.

### Pipe hardening (also in phase 1)
- The pipe ACL denies the NETWORK SID (no remote SMB callers) and grants Authenticated Users only ReadWrite (no creating server instances); real authorization is the per-connection check.
- The client verifies the pipe server's process id (`GetNamedPipeServerProcessId`) against the service's PID from `sc queryex` before sending anything, so a process that squats the pipe name while the service is down never receives a private key. The verified PID is cached per `RpcOps`.
