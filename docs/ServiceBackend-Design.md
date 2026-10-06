# Service back-end (5.0) - design and reference

Status: **implemented on branch `service-backend`**, reviewed twice for security (see [`ServiceBackend-Security.md`](ServiceBackend-Security.md)). The manual test plan at the end still has to be run on real machines. The user-facing description is in [`MANUAL.md`](MANUAL.md) chapter 37; the codebase summary is in [`../CLAUDE.md`](../CLAUDE.md).

## 1. Why

Until 4.6 everything privileged ran inside the UI process, so `MasselGUARD.exe` was `requireAdministrator` (a UAC prompt at every launch; "Start with Windows" worked around it with an elevated scheduled task). A Windows service takes over the privileged operations, so that:

- the app itself starts without UAC,
- the network rules can keep running with no window (and before anyone signs in),
- the Explorer right-click bypass and the CLI have a real endpoint instead of "talk to the elevated window",
- a LocalSystem process reads its files from a local folder it controls.

**Direct mode stays supported**: when the service is not installed (portable copies, Scoop, standard users without the service) the app behaves exactly as 4.6: it elevates itself (UAC, or the scheduled task) and does the privileged work in-process.

## 2. Decisions

| Topic | Decision |
|---|---|
| Architecture | Thin privileged service (option A) plus an optional headless fallback (snapshot handover). The window stays the owner of `config.json` and history; the service never reads a user's config. |
| Two back-ends | `IPrivilegedOps` seam with two implementations: in-process (direct mode) and a named-pipe proxy (service mode). Chosen once at startup (`PrivilegedBackend.Select`). |
| Tunnel configs | A service-owned store under `%ProgramData%`, machine-scope DPAPI. The window pushes a config only when its hash differs. In direct mode the per-user DPAPI files are used as before. |
| Pre/post connect scripts | Run in the user's window process (never as SYSTEM). They do not run in headless automation. |
| Who may call | Administrators (elevated token) and the users listed in `service-users.txt` (the installing user plus `service allow`). Checked per connection through the client token. |
| Portable use | Direct mode. "Install service" from a portable copy installs MasselGUARD to a local folder first. |
| Updates | The in-app updater verifies the zip against a `.sha256` asset of the same release and refuses to update without one (section 8). Code-signing is not used. |
| Version | 5.0 (the change alters how the app is installed and run). |

## 3. Components

| Piece | File | Notes |
|---|---|---|
| Seam interfaces | `Services/PrivilegedOps.cs` | `ITunnelOps`, `IKillSwitchOps`, `IDnsOps`, plus service-mode extras `IStoredConnectOps`, `IDnsHoldOps`, `IAutomationOps`; `TunnelDllOps` (in-process tunnels), `FakeOps` (selftests). `KillSwitchService` and `DnsService` implement their interfaces directly. |
| RPC | `Services/PrivilegedRpc.cs` | `RpcRequest/Response`, `RpcValidator`, `RpcDispatcher`, `RpcServer`, `RpcOps` (client), `RpcAuth`/`RpcCaller`. |
| Back-end choice | `Services/PrivilegedBackend.cs` (GUI) | Service when installed and answering (server PID verified), else direct. |
| Service host | `Services/ServiceHost.cs` (GUI exe, `MasselGUARD.exe /svc`, LocalSystem) | Starts the RPC server, recovers stale firewall rules and DNS at start, restores DNS and firewall at stop. |
| Installer | `Services/ServiceInstaller.cs` | `sc.exe` by full path; folder check; elevation-aware (`InstallAuto`, `UninstallAuto`); `AllowUser`/`DenyUser`; PID via `QueryServiceStatusEx`. |
| Folder trust | `Services/SecureFolders.cs` | `Ensure` (data folders), `CheckInstallFolder` (exe folder). |
| Tunnel store | `Services/TunnelStore.cs` | `FileTunnelStore`; `StoredConnect` lives in `HeadlessAutomation.cs`. |
| Timed DNS hold | `Services/DnsHoldKeeper.cs` | Pure; ticked by a 1 s timer in the service. |
| Headless automation | `Services/HeadlessAutomation.cs` (shared: pure planner, snapshot, lease) and `Services/HeadlessHost.cs` (GUI project, inside the service) | |
| Bypass without a window | `Services/BypassPlan.cs` + `Services/BypassClient.cs`; the Explorer entries run `MasselGUARD.exe --bypass` | |
| Autostart | `Services/AutostartRunKey.cs` + `MainWindow.ApplyAutostart` | |
| Install over a running copy | `Services/InstallFiles.cs` | |
| Update verification | `UpdateChecker.ParseChecksum` / `Sha256OfFile` | |

All shared files are listed in `MasselGUARDcli/MasselGUARDcli.csproj` and are WPF-free; the selftest covers them (see [`CliSelfTest.md`](CliSelfTest.md)).

## 4. Startup and elevation

`app.manifest` is `asInvoker`. `Program.Main`:

1. `/service <conf>` (a tunnel service child) and `/svc` (the service) are handled first, before any WPF.
2. Elevated: continue.
3. Not elevated and the service is running and answers the pipe (`ServiceUsable`, which also waits up to about 10 s while the service is starting at logon): continue **unelevated**.
4. Otherwise direct mode: the scheduled task `MasselGUARD` if it exists, else `RelaunchElevated` (UAC; declining ends the app).

Actions that need administrator rights while unelevated call `MainWindow.EnsureElevated()`, which offers to restart the app elevated; the user repeats the action there. They are: install/uninstall, Start with Windows when a scheduled task is involved, the HKLM DNS-leak policy, orphaned-service removal, and updating an installed copy (`UpdateChecker.NeedsElevation`). Service install/removal from an unelevated app starts `MasselGUARDcli.exe service install|uninstall` through one UAC prompt and records the calling user as allowed. The single-instance mutex and the show-window event carry an ACL for Authenticated Users so an elevated and an unelevated instance see each other.

## 5. The RPC

One JSON line per request over the pipe `MasselGUARD.Service`, one JSON line back, one connection per call; requests are capped at 2 MB (a config snapshot is the largest) with 10 s read/write timeouts.

- **Pipe ACL:** SYSTEM and Administrators full, Authenticated Users read/write (no creating server instances), the NETWORK SID denied (no remote callers).
- **Caller check:** inside `RunAsClient` the server builds an `RpcCaller(Sid, IsAdmin)`: an elevated Administrators token, or a SID listed in `%ProgramData%\MasselGUARD\service-users.txt`; everyone else gets "access denied".
- **Client check:** before sending anything the client compares the pipe server's process id with the service's own (`GetNamedPipeServerProcessId` vs `QueryServiceStatusEx`), so a process that squatted the pipe name never receives a config.
- **Validation:** `RpcValidator` checks every argument before anything touches the system: tunnel names (letters/digits and a few separators, no path/quote characters, no reserved device names), configs (size, `[Interface]` + `PrivateKey`, `PreUp/PostUp/PreDown/PostDown` refused), interface GUIDs, address families, endpoint IPs, bypass ranges (no catch-all), split ranges, DNS profiles (IPs, https DoH template without shell characters), hold lengths (1-3600 s), snapshot size.
- **Injected predicates** (the service supplies them): `tunnelAllowed` (no service of that name, or one running our exe) and `interfaceAllowed` (adapter up, not loopback or a tunnel interface).

| Group | Ops |
|---|---|
| General | `ping` |
| Tunnels | `tunnel.connect` (config in the request), `tunnel.connectstored` (by name + config hash + split intent), `tunnel.disconnect` |
| Store | `store.put`, `store.prune` |
| Kill switch | `ks.enable`, `ks.disable`, `ks.disableall`, `ks.cleanup` |
| DNS | `dns.apply`, `dns.auto`, `dns.restore`, `dns.restoreall`, `dns.has`, `dns.count` |
| Timed DNS | `dns.hold`, `dns.hold.cancel`, `dns.hold.stop`, `dns.hold.status`, `dns.release` |
| Handover | `auto.push`, `auto.lease`, `auto.release` |

Read-only status (is a tunnel running, byte counters) stays local in the window: it needs no privileges. New RPC ops need a validator branch **and** selftest cases.

## 6. Data and folders

`%ProgramData%\MasselGUARD\` (created by `SecureFolders.Ensure`: protected DACL, SYSTEM + Administrators, owner must be SYSTEM/Administrators; a pre-existing folder owned by someone else or a junction is renamed aside to `*.untrusted-<stamp>`):

| Path | Content |
|---|---|
| `service-users.txt` | SIDs allowed to call the service (besides Administrators) |
| `tunnels\*.tun` | stored tunnel configs (machine-scope DPAPI + app entropy, hashed file names, per-entry owner SID). **No inheritance**: machine-scope DPAPI decrypts for any local process that can read the file |
| `run\<guid>\<name>.conf` | plaintext config staged for one connect only (tunnel.dll names the tunnel after the file); swept at service start |
| `automation.json`, `automation.owner` | the last pushed config snapshot and its owner SID |
| `dns_state.json` | the pre-override DNS snapshot (SYSTEM has no useful `%APPDATA%`) |
| `logs\service.log` | the only folder normal users may read |

The folder the service runs from is checked at install and at every service start (`CheckInstallFolder`): the folder, every parent, the exe and the DLLs must not be changeable by non-administrators, and no part may be a junction. Install MasselGUARD to Program Files before registering the service.

## 7. Features built on the service

- **Connect by name.** `TunnelService` uses `IStoredConnectOps` in service mode: it sends the tunnel name, the sha256 of the base config and the split intent; the service answers `stale` when it has no or a different config, the window pushes it (`store.put`) and asks again. The service applies the split rewrite (shared `RouteBasedBackend`, same fail-safe), re-validates and stages the file only for the connect. The orphan-scan stamp (`Description` = "MasselGUARD Tunnel: ...") is written in `TunnelDllOps.Connect`. `MainWindow.PruneServiceStore` removes configs of deleted tunnels.
- **Timed DNS override.** `DnsHoldKeeper` holds the profile id, the interfaces and an absolute UTC end; the service restores them 3 s after the end (the grace lets a running window's own end-of-override, which re-applies automation, win). `MainViewModel.UseDnsTemporarily` registers the hold, every manual DNS action cancels it (`ClearDnsTemp`), window exit calls `dns.release` (restore everything except held interfaces), startup adopts a running hold. A hold does not survive a service restart.
- **Bypass without the window.** `BypassClient` (shared by `MasselGUARDcli dns bypass` and the windowless launcher `MasselGUARD.exe --bypass <seconds|stop|toggle>`) asks the window through `CommandPipe`; with no window and the service answering, `BypassClient.ViaService` reads the user's config (the CLI runs as the user), builds a `BypassPlan` (profile, connected adapters, families, length; pure, same error texts as the window) and calls `dns.apply` + `dns.hold`; `stop`/`toggle` use `dns.hold.stop`/`dns.hold.status`. The Explorer right-click entries run the launcher `MasselGUARD.exe --bypass ...` (the GUI exe has no console, and since 5.0 no UAC, so no terminal window appears; a failure shows a message box); the entries are rewritten at every start of MasselGUARD, so entries from older versions that pointed at the console CLI are replaced automatically.
- **Start with Windows.** With the service installed: a per-user Run entry (`HKCU\...\Run\MasselGUARD`, no admin rights, starts unelevated). Without it: the elevated scheduled task. `ApplyAutostart` removes the other mechanism; installing the service migrates an enabled autostart, removing it moves it back when the caller can create the task.
- **Installing over a running copy.** The service, every connected tunnel (`MasselGUARD.exe /service`) and a window hold the exe open. `InstallFiles.CopyOverwriting` renames a locked file to `name.old-<stamp>` and copies the new one in (Windows lets a running file be renamed, not overwritten); the update batch does the same; leftovers are deleted at the next start of the app or the service.

## 8. Updates and release checksums

The in-app updater downloads `MasselGUARD-<arch>.zip` from the latest GitHub release and copies it over the install with an elevated batch, which also stops and restarts the service around the copy. With the service installed that code runs as SYSTEM, so the download is verified:

- **Release time:** `BUILD.bat` writes `dist\MasselGUARD-<arch>.zip.sha256` (`<hash>  MasselGUARD-<arch>.zip`, lowercase sha256) next to each zip. Upload **both** files of **both** architectures as release assets.
- **Update time:** `FetchLatestReleaseAsync` finds the `.sha256` asset of the same zip; `UpdateAsync` downloads it, hashes the downloaded zip and only extracts it when they match. A missing or malformed checksum file, or a mismatch, aborts the update with an explanatory message and nothing is changed (fail closed).
- **What it covers:** corrupt, truncated or swapped downloads. It does **not** cover someone who controls the GitHub release itself (they could replace both files); that would need code-signing, which is not used. The 4.6.0 updater does not check checksums; the check applies from the first 5.0 build onwards.

## 9. Headless automation (snapshot handover)

`AppConfig.HeadlessAutomation` (Settings > Automation, default off, in the `automation` preset block). Needs the service.

- **Window side** (service mode, `MainWindow.StartAutomationSync`): `auto.lease` every 5 s renews a 20 s `UiLease`; at startup and 1.5 s after every config save `PushAutomationNow` sends the serialised `AppConfig` (`auto.push`: 1 MB, validated by `AutomationSnapshot.TryParse`, which drops inline tunnel configs, file paths and tunnels with invalid names) and, only when the feature is on, the decrypted tunnel configs (`store.put`, skipped when unchanged). The service persists the snapshot so it works after a reboot before sign-in.
- **Service side** (`HeadlessHost`, every 2 s): snapshot with the feature on and the lease lapsed -> take over; a live lease -> hand back. On takeover `HeadlessPlanner.Seed(snap, seedDns:false)`: tunnels are not re-evaluated for the network that is already there (a manual disconnect before closing stays), DNS is applied once for the connected adapters. After that each settled network change (`NetworkWatcher` + `WiFiService` for the SSID) goes through the pure `HeadlessPlanner`: `RuleEngine.EvaluateNetwork` / `EvaluateWifiDisconnected` -> disconnect all or activate a target (others stopped first; only local tunnels that exist in the snapshot); `DnsPolicy.Evaluate` per changed adapter, restore for adapters that left, held while any tunnel runs, skipped for adapters under a bypass hold. Connect goes through `StoredConnect.Run`, the same path as connect-by-name, and enables the kill switch when `KillSwitchMode == always` or the tunnel's flag is set. Every tunnel it touches passes the name checks first (valid name, no foreign service of that name).
- **Exit behaviour:** with the feature on and the service in use, `App.OnExit` leaves tunnels and the kill switch in place and only releases the lease.
- **Owner:** the first user to turn the feature on becomes the owner (`automation.owner`); other non-administrators cannot push, renew or release; administrators can.
- **Not done headless:** history entries, toasts, data-cap warnings/kills, schedule rules, pre/post connect scripts, auto-reconnect, companion tunnels. The setting says so.
- **Gaps by design:** the planner acts on changes only (a tunnel in the wrong state for the current network is not corrected until the network changes); a network change in the 20 s between a crashed window's last heartbeat and the takeover is handled by nobody.

## 10. Manual test plan (real machine)

Prerequisite: MasselGUARD installed in Program Files (Settings > Startup > Install). Run in this order; each step needs the previous ones.

1. **Install.** Settings > Startup > Background service > Install service (one UAC prompt). Status says running; `MasselGUARDcli service status` agrees. Start MasselGUARD: the activity log (Debug level) shows `Back-end: MasselGUARD service`.
2. **No UAC.** Start the exe from Explorer as a normal user: no UAC prompt. `sc stop MasselGUARDsvc` (admin) and start again: UAC prompt and direct mode as in 4.6. Diagnostics says "not elevated, using the service" in service mode.
3. **Tunnels.** Connect/disconnect, with and without the kill switch, with a split-tunnel tunnel; edit the config and reconnect (new config used); delete a tunnel (its `.tun` disappears at the next prune); `tunnels\temp` next to the exe stays empty.
4. **DNS.** Apply a DNS profile; start a 5-minute bypass, close the window from the tray: DNS stays on the bypass resolver; reopen: countdown continues; close and wait: DNS restored about 3 s after the end; Stop now: restored at once.
5. **Bypass without a window.** Close MasselGUARD, `MasselGUARDcli dns bypass 30` as a normal user: DNS switches and returns after about 33 s; `stop` and `toggle` work; the Explorer right-click entries do the same without any terminal window (an error, for example no bypass profile marked, shows a message box).
6. **Autostart.** Start with Windows on: no restart prompt, `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MasselGUARD` shows the exe; sign out/in: starts without UAC and uses the service. Enable it first without the service, then install the service: the old task is replaced by the Run entry.
7. **Headless automation.** Feature on, one SSID rule (tunnel A on network X, empty tunnel on Y). Exit MasselGUARD from the tray: tunnel A stays up and `logs\service.log` says the service took over; switch to Y: disconnected; back to X: connected; reopen the window: "handed back". Feature off: exit disconnects tunnels as before.
8. **Install and update with things in use.** Install over a copy while the service and a tunnel run: succeeds, `*.old-*` leftovers disappear later. Update with a tunnel connected: needs two releases, the newer one with its `.sha256` assets; also try a release without them (the update must be refused with the message) .
9. **Folder rules.** Registering the service from a user-writable folder (for example `C:\MasselGUARD` created in Explorer) fails with the folder message; as a standard user `mkdir C:\ProgramData\MasselGUARD` before the first install: the installer moves it aside.
10. **Users.** `service allow --user OTHER`, then as OTHER: the service answers; OTHER cannot overwrite your stored tunnel or push automation; `service users` lists who is allowed.
11. **Uninstall.** Remove service (button): the app asks to restart and runs in direct mode. Uninstall MasselGUARD with the service installed: both go.
