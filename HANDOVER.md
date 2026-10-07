# MasselGUARD - Session Handover

**Project:** MasselGUARD - WireGuard + DNS automation manager for Windows (.NET 10; WinExe GUI `MasselGUARD.exe` + console `MasselGUARDcli.exe`).
**Current:** **5 - Background Badger** (the service back-end, the Browse DNS servers picker, single-number versions) is built and prepared on the branch `service-backend`; **4.6.0 - Wired Weasel** is the latest published release. The version in code is the bare number `5`; the first release tag is `5.0`.
**Where to start:** [`docs/Handoff-next-version.md`](docs/Handoff-next-version.md) (state of 5.0, what is left before a release, rules and traps), [`docs/ServiceBackend-Design.md`](docs/ServiceBackend-Design.md), [`docs/ServiceBackend-Security.md`](docs/ServiceBackend-Security.md). Architecture: [`CLAUDE.md`](CLAUDE.md). User-facing history: [`docs/WHATSNEW.md`](docs/WHATSNEW.md).
> **Standing rules (carry every cycle):**
> 1. **`Models/*.cs` stay WPF-free** - shared with the CLI.
> 2. **The CLI lists shared `Services/*.cs` explicitly** in `MasselGUARDcli/MasselGUARDcli.csproj` (it globs Models, not Services) - a new shared Service must be added there.
> 3. **Lang files:** every user-facing string goes through `Lang.T`; new keys go into **all 12** `lang/*.json` with full parity and `{0}` placeholder integrity, validated with the app's own parser (`System.Text.Json`). The activity log, CLI, self-test output and the diagnostics "Copy all" report stay **English** (see the Localization bullet in `CLAUDE.md`).
> 4. **No em-dashes** anywhere in text (UI, lang files, docs, theme descriptions) - use a hyphen.
> 5. **Native DLLs** live in `wireguard-deps\<arch>\`; a release needs **both** `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip`.
> 6. The user runs `BUILD.bat`, tests the (elevated) app and commits. The assistant only `dotnet build`-verifies and never commits.

---

## 1. Release checklist

The checklist for release 5 is in [`docs/Handoff-next-version.md`](docs/Handoff-next-version.md) section 2. In short: run the manual test plan, bump the version everywhere (section 2 below), run a plain `BUILD.bat`, create the GitHub release **with both zips and both `.zip.sha256` files**, bump the Scoop bucket (`../MasselGUARD-scoop`: version + both SHA256 hashes) and push `../MasselGUARD-themes` when themes changed.

---
## 2. Version-bump checklist (every release)

From 5 on a release is a single number (5, 6, 7...); `UpdateChecker.ParseVersion` reads `5`, `5.0` and `5.0.0` as the same version. The FIRST tag is `5.0` (like the existing tags such as `4.6.0`, no `v`; `5.0` is read too) (installed 4.x copies need a tag with a dot to see it); later tags may be `6`, `7`.

- `UpdateChecker.cs` - `CurrentVersion` **and** `_codenames["5"]`.
- Both csproj files - `Version`, `AssemblyVersion` (`N.0.0.0`), `FileVersion`, `InformationalVersion`.
- `BUILD.bat` - `VERSION` **and** `CODENAME` (the build appends `.0` for the assembly version and `.<YYMMDDHHMM>` for the InformationalVersion).
- Doc headers: the "Current version" line in `CLAUDE.md`, this file, `docs/Handoff-next-version.md`.
- `docs/WHATSNEW.md` - new `## vN - <Codename>` entry on top; `docs/release-body-N.md` for the GitHub release text.
- GitHub release/tag (zips **and** `.zip.sha256` files) + Scoop manifest (user's manual steps).

---
## 3. Open items

### A. Needs a live-tunnel check
- **Issue #50 - WireGuard-native stats + last handshake** read from the UAPI management pipe (`TunnelDll.GetWireGuardStats`, NetworkInterface fallback). Built and wired in 4.0.0; confirm on a connected tunnel that the status-dot tooltip shows a "Last handshake" line (no line = it fell back, still shows bytes).
- **Split tunneling** (route/IP-based) exclude/include routing and its kill-switch interplay.
- **ARM64** build not yet smoke-tested on real ARM64 hardware (DLLs are PE-verified `0xAA64`).

### B. Licensing - one verify item
- **`wireguard.dll` (wireguard-nt)**: confirm the exact licence and prebuilt-DLL redistribution terms upstream (<https://git.zx2c4.com/wireguard-nt>) and update `THIRD-PARTY-NOTICES.md` (currently marked *VERIFY UPSTREAM*). `tunnel.dll` (wireguard-windows/-go) is MIT. `LICENSE` (MIT) and the About-page credits are in place.

### C. Parked - per-app split tunneling
Prototyped on WinDivert and reverted (2026-09-10/11); the design and findings are in [`docs/SplitTunneling-Design.md`](docs/SplitTunneling-Design.md) §11. If revisited: WinDivert is dual **LGPLv3 / GPLv3** - take the **LGPLv3** path (dynamic-link an unmodified `WinDivert.dll`, ship its LICENSE + LGPL/GPL texts, keep the DLL user-replaceable), credit Basil (basil00) in About + `THIRD-PARTY-NOTICES.md`, plan an AV-allowlisting note, and note WinDivert 2.2.2 has no arm64 driver.

---

## 4. Prior releases (condensed)
- **5 - Background Badger:** the Windows service back-end (no UAC at start, headless automation, timed DNS override and bypass without the window, per-user autostart, machine-encrypted tunnel store, verified updates), the Browse DNS servers picker with an online list (`MasselGUARD-dnslist`), configurable bypass shortcut and default length, Test rule window, clearer DNS leak protection, single-number versions.
- **4.6.0 - Wired Weasel:** wired-network rules (SSID, DNS suffix, gateway MAC, subnet, connection type, adapter), several conditions per rule, rule order = table order, create a rule from the current network, timed DNS bypass with Explorer menu and CLI, lighter on memory, resizable lists.
- **4.5.0 - Resolving Raven:** the UI/settings overhaul (General > Feature settings, split Settings tabs, themeable section icons, Time | Event activity log, drag-reorder, fully translated interface).
- **4.2.0 - Resolving Raven (beta):** DNS automation (per-network resolver, plain or DoH, tunnel-independent), DNS profiles panel + DNS in the charts, connect on start / start minimized, built-in tester, companion (WireGuard-for-Windows) mode removed.
- **4.0.0 - Forking Fox:** route/IP-based split tunneling, non-elevated `help`/`version`/`selftest`, licensing groundwork (MIT `LICENSE`, `THIRD-PARTY-NOTICES.md`), ARM64 `tunnel.dll`.
- **3.9.5 - Selective Serval:** 12 languages, cap usage rings + chart, kill-at-cap, directional trusted-network rules, tunnel export (`.mgconf`/QR).
- **3.9.0 - Adaptive Armadillo:** native x64 + ARM64 builds.
- **3.8.0 - Protective Pangolin:** never shipped standalone (folded into 3.9.0).
- **3.7.1 - Chromatic Chameleon:** last release before the 3.8/3.9 line; installs in the wild may still be on it, hence the legacy `MasselGUARD.zip` bridge.
