# MasselGUARD - Session Handover

**Project:** MasselGUARD - WireGuard + DNS automation manager for Windows (.NET 10; WinExe GUI `MasselGUARD.exe` + console `MasselGUARDcli.exe`).
**Current:** **4.5.0 - Resolving Raven** on branch `dev` (feature work happens on `dev`; no per-version branches).
**Shipped tags:** `4.0.0` (Forking Fox, final), `4.2.0-BETA` and `4.5.0-beta` (Resolving Raven betas). The version in code stays numeric (`4.5.0`) - a `-beta` suffix would break `UpdateChecker.ParseVersion`; "beta" lives only in the GitHub tag.
**What 4.5.0 is:** the UI/settings overhaul - General > Feature settings, split Settings tabs, consistent main-window layout, themeable section icons (Theme Builder "Section icons" editor), Time | Event activity log + pop-out window, drag-reorder with drop lines, WireGuard/DNS may both be off, and a fully translated interface. Full list: [`docs/WHATSNEW.md`](docs/WHATSNEW.md). Architecture: [`CLAUDE.md`](CLAUDE.md).

> **Standing rules (carry every cycle):**
> 1. **`Models/*.cs` stay WPF-free** - shared with the CLI.
> 2. **The CLI lists shared `Services/*.cs` explicitly** in `MasselGUARDcli/MasselGUARDcli.csproj` (it globs Models, not Services) - a new shared Service must be added there.
> 3. **Lang files:** every user-facing string goes through `Lang.T`; new keys go into **all 12** `lang/*.json` with full parity and `{0}` placeholder integrity, validated with the app's own parser (`System.Text.Json`). The activity log, CLI, self-test output and the diagnostics "Copy all" report stay **English** (see the Localization bullet in `CLAUDE.md`).
> 4. **No em-dashes** anywhere in text (UI, lang files, docs, theme descriptions) - use a hyphen.
> 5. **Native DLLs** live in `wireguard-deps\<arch>\`; a release needs **both** `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip`.
> 6. The user runs `BUILD.bat`, tests the (elevated) app and commits. The assistant only `dotnet build`-verifies and never commits.

---

## 1. Release checklist - 4.5.0

1. **Visual/functional check** of the 4.5.0 changes on a real run (layout + icon sizes, Theme Builder Section icons, drag-and-drop drop lines, one-time column-width reset, both features off, a non-English language incl. toasts and System diagnostics).
2. **Commit** `dev` (MasselGUARD) and **push `../MasselGUARD-themes`** (Resolving Raven theme with custom icons, em-dash cleanup, updated `THEME_EXAMPLE.md`); add Resolving Raven's **preview screenshots** to the themes repo.
3. **Tag/release** with both arch zips (plus the legacy x64 `MasselGUARD.zip` bridge for pre-3.8 installs).
4. **Scoop bucket** (`../MasselGUARD-scoop`): bump `version` + both SHA256 hashes, or confirm the Excavator workflow did it.

---

## 2. Version-bump checklist (every release)

- `UpdateChecker.cs` - `CurrentVersion` **and** `_codenames["x.y.z"]`.
- `BUILD.bat` - `VERSION` **and** `CODENAME`.
- Doc headers: `CLAUDE.md`, `docs/MANUAL.md`, `docs/CLIManual.md`, `docs/Reference.md`, `README.md`.
- `docs/WHATSNEW.md` - new `## vX.Y.Z - <Codename>` entry.
- GitHub release/tag + Scoop manifest (user's manual steps).

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
- **4.2.0 - Resolving Raven (beta):** DNS automation (per-network resolver, plain or DoH, tunnel-independent), DNS profiles panel + DNS in the charts, connect on start / start minimized, built-in tester, companion (WireGuard-for-Windows) mode removed.
- **4.0.0 - Forking Fox:** route/IP-based split tunneling, non-elevated `help`/`version`/`selftest`, licensing groundwork (MIT `LICENSE`, `THIRD-PARTY-NOTICES.md`), ARM64 `tunnel.dll`.
- **3.9.5 - Selective Serval:** 12 languages, cap usage rings + chart, kill-at-cap, directional trusted-network rules, tunnel export (`.mgconf`/QR).
- **3.9.0 - Adaptive Armadillo:** native x64 + ARM64 builds.
- **3.8.0 - Protective Pangolin:** never shipped standalone (folded into 3.9.0).
- **3.7.1 - Chromatic Chameleon:** last release before the 3.8/3.9 line; installs in the wild may still be on it, hence the legacy `MasselGUARD.zip` bridge.
