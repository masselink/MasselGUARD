# Service back-end - security review log and accepted risks

The privileged part of MasselGUARD runs as a LocalSystem service that standard users may call, so it was reviewed twice (2026-10-06, `/security-review` on the branch). This file records what was found, what was done, and what is knowingly left. Design and mechanics: [`ServiceBackend-Design.md`](ServiceBackend-Design.md).

## Threat model in one paragraph

A local, authenticated, non-administrator user (or a process running as one) tries to get SYSTEM rights, to change machine-wide network state they should not (firewall policy, DNS for other users, someone else's tunnels), or to read secrets (tunnel private keys). Remote callers and administrators are out of scope (administrators can already do all of it). The service must also not trust a folder, a pipe name or a user-supplied name just because it exists.

## Findings and fixes

| Finding | Fix |
|---|---|
| **Service runs from a folder standard users can write to** (replace the exe or a DLL = SYSTEM) | `SecureFolders.CheckInstallFolder`: folder owner and DACL, every parent (no delete/rename/re-permission by non-administrators, no junctions), exe and DLLs. Checked at install and at every service start; the service refuses to run from an unsafe folder. |
| **`%ProgramData%\MasselGUARD` pre-created by a standard user** (owner can re-ACL it: edit `service-users.txt`, read staged keys, plant links for SYSTEM file writes) | `SecureFolders.Ensure`: protected DACL, owner must be SYSTEM/Administrators; an existing folder with another owner, or a junction, is renamed aside. Used by the installer and the service for every data folder; the data folder is admin-only, the log has its own readable `logs` folder. |
| **Too much power for listed (non-admin) users** | Kill-switch cleanup/disable never lower a firewall policy the service did not raise; DNS only on connected, non-loopback, non-tunnel adapters; tunnel names that exist as a service MasselGUARD did not create are refused (connect, stored connect, disconnect, headless automation); catch-all bypass ranges refused; reserved device names refused as tunnel names; stored tunnels belong to the user who stored them (no overwrite or prune by others). |
| **Headless automation acted on any user's snapshot** | One owner (the first user to enable it, persisted); other non-administrators cannot push, renew or release; administrators can. |
| **Second review: headless path skipped the name checks** (a snapshot tunnel named like a WireGuard-for-Windows tunnel could stop it) | `HeadlessHost` filters its tunnels through name validation and the foreign-service check before any state, disconnect or connect; `StoredConnect.Run` enforces them for every caller; `AutomationSnapshot.TryParse` drops invalid names. |
| Hardening | `sc.exe` by full path; service PID from `QueryServiceStatusEx`; pipe denies the NETWORK SID; the client verifies the pipe server's PID before sending a config; the install folder and the data folder are re-verified rather than trusted; machine-scope DPAPI folder has no inheritance. |
| Updates | The updater verifies the downloaded zip against the release's `.sha256` asset and refuses to update without one (see Design section 8). |

## Accepted risks and things left on purpose

- **A listed user can connect any stored tunnel config and set DNS on real adapters.** That is the VPN feature itself; listing a user means trusting them with it. Administrators decide who is listed (`service allow|deny|users`). A tunnel also changes routes for the whole machine.
- **The update check is not a signature.** A checksum published in the same GitHub release protects against corrupt or swapped downloads, not against someone who controls the release. Code-signing would be needed for that; it is not used.
- **First enabler owns headless automation.** A listed user can enable it before the intended user does (the administrator can still replace the snapshot).
- **Portable and direct mode are unchanged:** an elevated app still runs privileged work in-process, from wherever the exe is.
- **Update batch location:** the elevated update batch lives in `%TEMP%` as before (pre-existing); the checksum is verified before the batch is written, which narrows but does not remove that window.
- **Headless automation is partial** (no history, toasts, caps, schedules, scripts, auto-reconnect), the planner acts on changes only, and a window crash leaves a 20 s gap. If the service is stopped or removed, tunnels it connected keep running unmanaged and its kill-switch rules stay until the service next starts. See Design section 9.

## Functional items still open

1. **Real-machine tests** of everything beyond installing the service (the plan is Design section 10).
2. **No automatic fallback to direct mode** when the service stops while the app runs: the window logs a warning after three missed heartbeats and the actions fail until the service runs or the app is restarted.
3. **Allowed users** can only be managed from the CLI (`service allow|deny|users`), not in the GUI.
4. **Translations** of the strings added for the service were written without a native speaker's review.
5. **Portable copies:** the service button on a portable copy installs MasselGUARD first, which is more than the word "service" suggests.
6. **Service-side error texts** (RPC errors) are English; they reach message boxes in some cases.
