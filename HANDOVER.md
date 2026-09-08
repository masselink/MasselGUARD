# MasselGUARD — Session Handover

**Project:** MasselGUARD — WireGuard tunnel manager for Windows (.NET 10; WinExe GUI `MasselGUARD.exe` + `MasselGUARDcli.exe` console).
**Ships from:** **3.9.5 — Selective Serval** (final, not beta; `docs/WHATSNEW.md` keeps its beta label by choice). 12 languages at **728-key parity**; cap rings redesigned (equal self-sizing rings, d/w/m glyphs, greyed-when-disconnected, pinned so the uptime text can't shove them); Behaviour popup + About page localized.
**Target:** **4.0.0 — Forking Fox** _("fork" = splitting traffic)._ **Version bump DONE** — code + docs now report 4.0.0 (§4 complete bar the GitHub tag/release).
**Headline:** **Split tunneling** — route/IP-based in 4.0.0; per-app split phased to a later 4.x (§2).
**Branch:** `dev`
**Status:** 4.0.0 in planning. **Licensing groundwork + About-page credits DONE** (§5–6); **both carry-over fixes (§3) DONE** this session (cap-killed marker coloured; per-tunnel Hide-cap-ring flag, lang key ×12, both builds clean). **Split-tunneling design (§2) DONE** → [`docs/SplitTunneling-Design.md`](docs/SplitTunneling-Design.md). **Split code steps 1–6 DONE:** `Services/CidrMath.cs` (set math) + `SplitTunnelBackend.cs` (`RouteBasedBackend`, conf-rewrite) wired into `TunnelService.Connect` (after decrypt, before validate; fail-safe); `StoredTunnel` split fields + `Models/SplitConfig.cs`; kill-switch exclude-bypass allow-rules; **Split tab in `TunnelConfigDialog`** (mode radios + ranges list + greyed Apps, validated) with **11 lang keys ×12 (parity 739)**; **export/import round-trip** (`TunnelExportService` SplitMode/SplitRanges comment lines; GUI+CLI import restore; Raw-tab reflect) + **CLI `info`** surfacing. Hidden `MasselGUARDcli selftest` = **35/35 green** (18 CidrMath + 11 backend + 6 export); all builds clean. **Step 7 DONE** — docs (WHATSNEW/MANUAL §5/CLIManual/Reference §43/CLAUDE.md) + **4.0.0 version bump** across `UpdateChecker.cs`, `BUILD.bat`, both `.csproj`, all doc headers; `version` → `v4.0.0 | Forking Fox`. **Also this session:** `MasselGUARDcli help`/`version`/`selftest` now run **without elevation** (`Program.cs` `IsNonElevatedCommand`; verified from a non-elevated shell — driver commands still gated). **Split tunneling is feature-complete + version cut.** ⚠️ Still to do (manual/release): (1) **manually test split on a live tunnel** (assistant can't run the elevated app); (2) build the ARM64 `tunnel.dll` (outstanding from 3.9.0); (3) tag/release `4.0.0` with **both** arch zips.

> ⚠️ **Standing build traps (carry every cycle):**
> 1. **`Models/*.cs` must stay WPF-free** — shared with the CLI; a `System.Windows.*` reference breaks the CLI build.
> 2. **The CLI lists shared `Services/*.cs` explicitly** in `MasselGUARDcli/MasselGUARDcli.csproj` (it globs Models, not Services). A new shared Service must be added there or it's CS0103 in the CLI only.
> 3. **Lang files:** any new key goes into **all 12** `lang/*.json`, then validate each with the app's exact parser — `[System.Text.Json.JsonDocument]::Parse` — not just PowerShell `ConvertFrom-Json` (which tolerates raw control chars the app rejects). Keep full key parity + `{0}` placeholder integrity. (A `[ \t]*` regex can trip the shell sandbox — a plain string-replace insert is the reliable fallback.)
> 4. **Native DLLs** live in `wireguard-deps\<arch>\`; a release needs **both** `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip`. ARM64 `tunnel.dll` still needs the llvm-mingw aarch64 toolchain (outstanding from 3.9.0).
> 5. User runs `BUILD.bat` and manual-tests (elevated app — the assistant does not run it). Assistant only `dotnet build`-verifies.

---

## 1. Release name — ✅ Forking Fox

Codename convention is **[Adjective + Animal], alliterative**, adjective hinting at the headline (Chromatic Chameleon → theming, Protective Pangolin → config locking, Adaptive Armadillo → multi-arch, Selective Serval → rules). **Forking Fox** ties "fork" to splitting traffic. (Fallbacks considered: Parallel Panther, Branching Badger, Quantum Quokka, Boundless Bison, Pioneering Puma.)

---

## 2. Headline feature — ✅ Split tunneling

> **✅ Full design written** → [`docs/SplitTunneling-Design.md`](docs/SplitTunneling-Design.md) (data model, effective-`AllowedIPs` CIDR-math, kill-switch/DNS coupling, backend abstraction, editor UI, export round-trip, WinDivert seams, 13-step build order). The summary below stays; the doc is authoritative. **Key finding:** MasselGUARD never touches the route table — `tunnel.dll` programs routes purely from `AllowedIPs`, so route-based split is a pure `AllowedIPs` rewrite on the plaintext `.conf` before `TunnelService.Connect` writes the temp file. **Next code step:** `CidrMath.cs` + unit tests (design §13).

Route/IP-based split ships in 4.0.0; **per-app split is phased to a later 4.x**. Options B–E (kept for reference) are at the end of this section.

### The two mechanisms
| | Route / IP-based (**4.0.0**) | Per-app (**later 4.x, via WinDivert**) |
|---|---|---|
| Splits by | destination IP range (`AllowedIPs` / route table) | process (which app) |
| How | manage the tunnel's `AllowedIPs` — `0.0.0.0/0` **minus** excluded ranges, or an explicit include list; adjust the route table | **bundle WinDivert** (its `.sys` is MS-signed) + user-mode steering: match each flow to its PID, re-inject on the chosen interface |
| Own kernel driver / signing? | **No** | **No** — stand on WinDivert's already-signed driver |
| Effort / risk | medium | high (integration + licensing, not signing) |

### Per-app feasibility (yes, without signing our own driver)
- WireGuard/wireguard-NT routes **only by destination IP** — no per-process concept, so per-app needs packet-level steering by process.
- **Chosen path: WinDivert.** Its kernel driver ships **Microsoft-signed**, so we bundle the signed binary and steer in **user mode** (connection→PID table → re-inject on tunnel vs physical interface). No EV cert, no WHQL, no self-signed driver. **exclude-apps** is the tractable first flavour; **include-apps** is harder.
- **Alternative:** **WinpkFilter / Windows Packet Filter** — a commercially-licensed, already-signed NDIS driver (what WireSock uses). Licence fee, avoids GPL/LGPL copyleft. A user-mode **WFP block filter** can hard-block an app but not reroute — only a partial substitute.
- **Ruled out:** our **own** unsigned kernel driver (Driver Signature Enforcement blocks it) and test-signing.
- **Licensing:** WinDivert is **LGPLv3 / GPLv3** — obligations in §5. Consider moving per-app onto **userspace WireGuard (wireguard-go)** for a cleaner packet path (WireSock-style) — bigger change.
- **Decision:** ship route/IP-based split in 4.0.0; build the data model + steering abstraction (below) so the WinDivert backend slots in later without reworking config or UI.

### Plan for 4.0.0 (route/IP-based) + WinDivert groundwork
- **Data model (design once, for both backends):** per-tunnel split config on `StoredTunnel` — `SplitMode` (`off`/`exclude`/`include`) + `SplitRanges` (IP/CIDR list) **and** a forward-looking `SplitApps` (app paths; unused/hidden in 4.0.0 but serialized so the schema doesn't change when WinDivert lands). WPF-free. Round-trips through `TunnelExportService`.
- **Steering abstraction (key prep):** an `ISplitTunnelBackend` interface with a `RouteBasedBackend` (4.0.0 — effective `AllowedIPs` + routes) and a future `WinDivertBackend` (per-app). Engine picks the backend from `SplitMode`/whether app rules exist.
- **Engine (4.0.0):** compute effective `AllowedIPs` from base config + `SplitRanges` (`0.0.0.0/0` minus excludes, or explicit includes); manage routes on connect/disconnect. Design **together with** the kill switch (excluded ranges must survive a kill) and DNS (leak handling).
- **UI:** a "Split tunneling" section/tab in the tunnel editor — mode selector + range list; a disabled/"coming in 4.x" **Apps** sub-list so the layout is final now.
- **CLI:** surface split config in `info` + import/export.
- **WinDivert prep (no dependency added in 4.0.0):** reserve a future `windivert\<arch>\` layout (WinDivert ships x64/arm64 `.dll`+`.sys`); do the §5 licensing groundwork **before** adding the dependency.

### Options not taken (reference)
- **B — WFP kill switch / firewall hardening:** machine-wide kill switch at the WFP layer. Shares plumbing with per-app split — good 4.x companion. Effort medium-high; fail-safe design critical.
- **C — Provider onboarding / location presets:** one-click import from providers + "connect to a location". Lower kernel risk, more product.
- **D — Cross-device config sync:** encrypted sync of tunnels+settings via a shared folder. Conflict handling is the hard part.
- **E — Usage & analytics 2.0:** grow the 3.9.5 usage chart into a full dashboard. Low risk, but weak as a solo 4.0 headline.

> **Next step:** ✅ done — design is in [`docs/SplitTunneling-Design.md`](docs/SplitTunneling-Design.md). Begin coding at design §13 step 1 (`CidrMath.cs` + tests).

---

## 3. Carry-over fixes to include in 4.0.0 — ✅ BOTH DONE

### Fix 3.1 — 🛑 cap-killed marker is not coloured — ✅ DONE
Added `Foreground="{DynamicResource Danger}"` to the marker `TextBlock` ([MainWindow.xaml:505](MainWindow.xaml:505)). The ⚡/🔓 badges already tint via `Foreground` in this app (glyphs render monochrome), so the emoji-colour caveat didn't bite — kept consistent with them. (If a future theme/font ever renders 🛑 as a colour emoji that ignores `Foreground`, swap for a vector `Path`/`Ellipse` `Fill="{DynamicResource Danger}"`.)

### Fix 3.2 — per-tunnel "Hide cap ring" — ✅ DONE
1. `StoredTunnel.HideCapRing` (bool, default `false`) — threaded through both dialogs (Result prop + `existingHideCapRing` ctor arg + `MainWindow` add/edit) like the `*CapKill` flags. **Not exported** (local UI pref — deliberately left out of `TunnelExportService.TunnelSettings`).
2. `CapRingsVisibility => AnyCapConfigured && !StoredTunnel.HideCapRing` ([TunnelEntryViewModel.cs:361](ViewModels/TunnelEntryViewModel.cs:361)). Re-evaluated on edit via `RebuildTunnelList()`.
3. A **"Hide usage ring"** `CheckBox` below the monthly row in the DATA-USAGE section of both `TunnelConfigDialog.xaml` + `TunnelMetadataDialog.xaml`. New lang key `TunnelDialogHideCapRing` ×12; the orphaned `TunnelDialogCapShow` ("Show in row", from the removed per-period toggle) stripped from all 12 — net parity back to **728**, all JsonDocument-validated. GUI + CLI builds clean.

---

## 4. Version-bump checklist (do once, at cut)

4.0.0 is numeric-clean (a `-beta` suffix breaks `UpdateChecker.ParseVersion`). Update **all** of:
- `UpdateChecker.cs` — `CurrentVersion = "4.0.0"` **and** `_codenames["4.0.0"] = "Forking Fox"`.
- `BUILD.bat` — `set VERSION=4.0.0` **and** `set CODENAME=Forking Fox`.
- `MasselGUARD.csproj` **and** `MasselGUARDcli.csproj` — `<Version>`/`<AssemblyVersion>`/`<FileVersion>`/`<InformationalVersion>` → `4.0.0` / `4.0.0.0`.
- Docs headers: `CLAUDE.md`, `docs/MANUAL.md`, `docs/CLIManual.md`, `docs/Reference.md`, `README.md`.
- `docs/WHATSNEW.md` — new `## v4.0.0 — Forking Fox` entry (drop the beta framing this cycle).
- GitHub release/tag = `4.0.0` (user's manual step).

---

## 5. Licensing & attribution — groundwork ✅ DONE (one verify item remains)

> Not legal advice. Authoritative sources: the exact upstream `LICENSE` files and the FSF LGPL/GPL FAQ. Lawyer for anything commercial-facing.

**Done this session** (the "no LICENSE / no attribution" gap is closed):
- **`LICENSE`** — MIT, `Copyright (c) 2026 Harold Masselink`.
- **`THIRD-PARTY-NOTICES.md`** — WireGuard trademark + the two bundled DLLs + .NET + a WinDivert placeholder (LGPL path).
- License notes added to `README.md`, `docs/MANUAL.md` (user-facing), `docs/Reference.md` (§42, technical).
- Credit + license copyright decision: an **AI can't hold copyright** → copyright is Harold Masselink only; AI disclosure is optional (README, not LICENSE).

**Remaining before a public release:**
1. **Verify `wireguard.dll` (wireguard-nt) exact licence + prebuilt-DLL redistribution terms** upstream ([git.zx2c4.com/wireguard-nt](https://git.zx2c4.com/wireguard-nt)). `tunnel.dll` (wireguard-windows/-go) is MIT — clean. This is the one open licensing item.
2. **When WinDivert is bundled (4.x):** it's dual **LGPLv3 / GPLv3**. Take the **LGPLv3** path — dynamic-link an **unmodified** `WinDivert.dll`, ship its `LICENSE` + the LGPL & GPL texts, preserve the user's ability to replace the DLL (LGPL §4). Add Basil (basil00) to the About credits + a notice entry. (A permissive MIT app **must** use the LGPL path, not GPL.) Some AV engines flag WinDivert's `.sys` — plan an allowlisting note.

---

## 6. Also done this session (3.9.5 final + 4.0.0 prep)

- **3.9.5 finalized** as `3.9.5` (not beta) — code was already numeric; only the GitHub tag/WHATSNEW carried `-beta`, and WHATSNEW keeps it by choice.
- **`BUILD.bat`** now copies `LICENSE` → `dist\<arch>\LICENSE.txt` and `THIRD-PARTY-NOTICES.md` into each arch dir, so both land in every release zip.
- **About page (`Views/SettingsWindow.xaml` + `.cs`)** — new **Credits** block (MasselGUARD — Harold Masselink · WireGuard — Jason A. Donenfeld / WireGuard LLC · .NET — Microsoft), WireGuard trademark line, **"Licensed under the MIT License."** with **View license** / **Third-party notices** links (`LicenseLink_Click`/`NoticesLink_Click` → open the bundled file next to the exe, fallback to GitHub in dev via `OpenLocalOrUrl`).
- **About page fully localized** — 11 new keys ×12 langs (`AboutBy`, `AboutWhatsNew`, `AboutReleaseNotesError`, `AboutThemeUpdatesTooltip`, `AboutCredits`, `AboutTrademark`, `AboutLicensedMit`, `AboutViewLicense`, `AboutThirdPartyNotices`, `AboutThemeUpdateOne`, `AboutThemeUpdateMany`). Proper nouns kept literal. Parity now **728**; `dotnet build` clean.

---

## 7. State at handover (from 3.9.5)
- **Cap rings** (`Views/CapRings.cs`) — equal horizontal rings; font-derived diameter (`RingDiameter()` from `Theme.FontSize`, `MeasureOverride` self-sizes); ink-centred d/w/m glyphs (localized `CapRingDay/Week/Month`); greyed via `Active` DP when disconnected; pinned in a right-hand Grid cell so the uptime text can't shove it.
- Usage computed for **all** tunnels each stats poll (not just active) so disconnected rings show real usage — [`MainViewModel`](ViewModels/MainViewModel.cs:491).
- **Behaviour popup** (formerly "Defaults"): localized, `Do nothing / 🚫 Disconnect all tunnels / <tunnel>`; footer + row badges sync on both Settings and popup paths.
- Enforcement model (kill-at-cap, overrides, toasts) unchanged — `CLAUDE.md` "Data-usage warnings" bullet.

---

## 8. Prior releases (condensed)
- **3.9.5 — Selective Serval** (current): 6 new languages → 12 total; localization backlog closed; cap usage rings + chart; kill-at-cap enforcement; directional trusted-network rules; tunnel export (`.mgconf`/QR).
- **3.9.0 — Adaptive Armadillo:** native **x64 + ARM64** (per-arch `dist\<arch>\` zips; `wireguard-deps\<arch>\`; arch-aware update/validation). **Outstanding:** build the ARM64 `tunnel.dll` (Go+CGO, needs llvm-mingw aarch64).
- **3.8.0 — Protective Pangolin:** never shipped standalone — folded into 3.9.0. WiFi rule kinds, `.masselguard` managed preset, health dot, data caps, QR export.
- **3.7.1 — Chromatic Chameleon:** last release actually shipped before the 3.8/3.9 line (installs in the wild are ≤3.7.1 → keep shipping the legacy `MasselGUARD.zip` x64 bridge).
