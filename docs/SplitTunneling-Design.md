# Split Tunneling — Design (4.0.0 "Forking Fox")

**Status:** design (no feature code yet). Target: **4.0.0** ships route/IP-based split; **per-app split** is phased to a later 4.x on WinDivert. This document is the authoritative design; `HANDOVER.md` §2 is a summary that points here.

**Headline:** route/IP-based split tunneling — decide which destination IP ranges go *through* the tunnel and which bypass it, per tunnel.

---

## 1. Why this is low-risk on our stack

MasselGUARD **does not manage the Windows routing table at all** — verified: no `netsh`/route/`Table=` handling anywhere in the codebase. `tunnel.dll` (wireguard-NT) installs and removes routes itself, derived **entirely from the peer's `AllowedIPs`**. WireGuard-Windows also auto-adds the pin-route to the `Endpoint` via the physical gateway, and handles teardown.

**Consequence:** route/IP-based split is a **pure `AllowedIPs` transformation**. We compute an *effective* `AllowedIPs`, write it into the plaintext `.conf`, and let wireguard-NT do all route programming. No new kernel surface, no route-table code, no driver. This is the whole reason it's a 4.0.0-sized feature and not a rewrite.

The one real coupling is the **kill switch** (§6): it blocks all outbound except the WG adapter + endpoint, so *excluded* (bypassing) traffic must be explicitly re-allowed out the physical NIC or the "split" silently becomes "blocked".

---

## 2. Two mechanisms (unchanged from HANDOVER §2)

| | Route / IP-based (**4.0.0**) | Per-app (**later 4.x, WinDivert**) |
|---|---|---|
| Splits by | destination IP range (`AllowedIPs`) | process (which app) |
| How | effective `AllowedIPs` = `0.0.0.0/0` **minus** excludes, or an explicit include list | bundle MS-signed `WinDivert.sys`; user-mode PID→flow steering, re-inject on chosen interface |
| Own driver / signing | **No** | **No** (stand on WinDivert's signed driver) |
| Effort / risk | medium | high (integration + LGPL licensing) |

Design goal: build the **data model + backend abstraction** now so the WinDivert backend slots in later with **no schema or UI rework**.

---

## 3. Data model (`Models/StoredTunnel.cs` — WPF-free, CLI-shared)

Add a small, forward-compatible block. All defaults keep existing tunnels behaving exactly as today (`SplitMode=off`).

```csharp
// ── Split tunneling ──────────────────────────────────────────────────
/// <summary>off = normal (AllowedIPs used verbatim); exclude = full tunnel
/// minus SplitRanges; include = only SplitRanges routed through the tunnel.</summary>
public string SplitMode { get; set; } = "off";           // "off" | "exclude" | "include"

/// <summary>IPv4/IPv6 CIDRs (or bare IPs) that define the split set. Meaning
/// depends on SplitMode. Empty list with a non-off mode = no-op (falls back to base).</summary>
public List<string> SplitRanges { get; set; } = new();

/// <summary>Forward-looking (per-app, WinDivert). Serialized + round-tripped in
/// 4.0.0 but UNUSED and hidden — reserved so the schema doesn't change when
/// WinDivert lands. Absolute exe paths.</summary>
public List<string> SplitApps { get; set; } = new();
```

Notes:
- `SplitMode` is a **string enum** (matches the codebase convention — `Source`, `AutoReconnectMode`, `TrustedWhen` are all string enums; `PresetService` serializes enums as strings).
- Keep it WPF-free (build trap #1). `List<string>` is fine for the CLI.
- `[JsonIgnore(WhenWritingDefault)]` is **not** needed — these are small and harmless in `config.json`; but consider `WhenWritingNull`/default only if we want clean files. Leave plain for readability.

---

## 4. Steering abstraction (`Services/ISplitTunnelBackend.cs` — add to `MasselGUARDcli.csproj`!)

One interface, two implementations. The engine picks a backend from the resolved split config.

```csharp
public interface ISplitTunnelBackend
{
    /// <summary>Rewrite the plaintext .conf so its effective AllowedIPs reflect the
    /// split config. Route-based backend does real work here; WinDivert backend
    /// returns the conf unchanged (it steers at packet level, not via AllowedIPs).</summary>
    string ApplyToConfig(string plaintextConf, SplitConfig split);

    /// <summary>Extra kill-switch allowances this backend needs (e.g. excluded
    /// ranges that must reach the physical NIC). Empty for include-mode/off.</summary>
    IReadOnlyList<string> KillSwitchBypassRanges(SplitConfig split);

    // Per-app hooks are no-ops in RouteBasedBackend; WinDivertBackend implements them later:
    void OnConnected(string tunnelName, SplitConfig split);   // start steering
    void OnDisconnected(string tunnelName);                   // stop steering
}
```

- **`RouteBasedBackend`** (4.0.0) — `ApplyToConfig` rewrites `AllowedIPs` (§5); `KillSwitchBypassRanges` returns the excluded ranges in exclude mode, empty otherwise; `OnConnected/OnDisconnected` no-op.
- **`WinDivertBackend`** (later 4.x) — `ApplyToConfig` returns conf unchanged; `KillSwitchBypassRanges` empty; `OnConnected` starts the PID→flow steering thread, `OnDisconnected` stops it.
- `SplitConfig` is a tiny DTO (`SplitMode`, `SplitRanges`, `SplitApps`) built from `StoredTunnel` so the backend never sees the whole model. WPF-free.

Backend selection (4.0.0): always `RouteBasedBackend` unless `SplitApps` is non-empty **and** a WinDivert backend is registered (never, in 4.0.0). Keep the selector trivial and central.

---

## 5. Effective `AllowedIPs` computation (the core algorithm)

This is the only non-trivial code. It's the well-known **WireGuard AllowedIPs calculator** (CIDR set subtraction), done per address family.

**Inputs:** base `AllowedIPs` from the `.conf` `[Peer]` (usually `0.0.0.0/0, ::/0` for a full tunnel), `SplitMode`, `SplitRanges`.

**`exclude` mode** (full tunnel minus excludes — the "protect everything except my LAN/printer/streaming service" case):
1. Start from the base set per family (v4 and v6 handled separately). If base is empty/absent, treat exclude-mode as "full tunnel" base (`0.0.0.0/0` + `::/0`) so exclusion has something to subtract from.
2. For each excluded CIDR, **subtract** it from the running set: replace any covering CIDR with the minimal set of CIDRs covering *its range minus the exclusion* (binary CIDR splitting — split the covering block in half repeatedly, keeping halves that don't overlap the exclusion, until aligned).
3. Result = a list of CIDRs that is `everything-in-base ∧ ¬excludes`. Excluded ranges now have **no route through the tunnel**, so they fall to the physical default route.

**`include` mode** (only these ranges tunneled — split VPN to specific subnets/services):
- Effective `AllowedIPs` = `SplitRanges` **intersected** with the base set (or just `SplitRanges` if base is `0.0.0.0/0`). No default route through the tunnel; everything else uses the physical NIC. Simplest case — no subtraction needed.

**`off` mode:** return the `.conf` verbatim.

**Implementation:** `Services/CidrMath.cs` (WPF-free, CLI-shared — **add to `MasselGUARDcli.csproj`**). Pure functions over `System.Net.IPAddress` + prefix length; operate on the address bytes as a big integer per family. Unit-testable in isolation (this is the piece most worth tests). Reuse `WireGuardConf`'s existing CIDR validation (`ValidateCidrList`) for input sanity.

**Applying it:** `RouteBasedBackend.ApplyToConfig` calls `CidrMath` to build the effective list, then uses the existing `Cli.WireGuardConf.Patch(conf, {"AllowedIPs": effective})` to rewrite the `[Peer]` line in place. `Patch` already replaces an existing key in its section. Multi-peer configs: for 4.0.0, apply to the **first `[Peer]`** and document that split targets single-peer tunnels (the 99% case); multi-peer split is a later refinement.

**Edge cases to cover in tests:**
- Exclude a range not in base → no-op.
- Exclude `0.0.0.0/0` entirely → empty v4 set (tunnel carries no v4; valid — v6 may still route).
- IPv6 base `::/0` with v4-only excludes → v6 untouched.
- Overlapping/duplicate excludes → union first, then subtract.
- Endpoint IP inside an excluded range → **fine**: wireguard-NT pins the endpoint route separately; exclusion of the endpoint's range doesn't strand the handshake.

---

## 6. Kill-switch coupling (must design together)

`KillSwitchService` (WFP via `HNetCfg.FwPolicy2`): sets `DefaultOutboundAction = Block` on all profiles and adds allow-rules for (a) the **WG adapter** (`Interfaces = [tunnelName]`) and (b) **UDP to the endpoint IP**. Loopback is allowed globally.

Problem: in **exclude mode**, excluded traffic is *meant* to leave via the physical NIC — but the kill switch blocks exactly that. Without handling, enabling both = excluded traffic is dropped, not bypassed.

**Fix:** when a tunnel with `SplitMode=exclude` **and** kill switch both apply, add allow-rules for each excluded range on the physical path:
- Extend `KillSwitchService.Enable(tunnelName, endpointIp, IReadOnlyList<string>? bypassRanges = null)` (optional 3rd arg — backwards compatible).
- For each bypass range, add `Prefix + "Allow_Split_" + tunnelName + "_" + i` with `RemoteAddresses = range`, direction out, all profiles. Remove them in `RemoveTunnelRules` (enumerate by name prefix, not fixed suffixes — current code removes two fixed names; switch to prefix-scan so N split rules clean up).
- Source of `bypassRanges`: `backend.KillSwitchBypassRanges(split)` — the excluded ranges (post-union, pre-subtraction). `include`/`off` → empty.

**Ordering:** unchanged — KS enables *after* the adapter is up (`TunnelService.Connect` line ~211). Bypass rules are just extra allows added in the same call.

**Cleanup:** `CleanupStaleRules` already removes everything under `Prefix` on startup — prefix-scan means split allow-rules are covered for free.

---

## 7. DNS coupling

- `DnsLeakService` checks whether DNS resolves through the tunnel. In **exclude** mode, DNS via the tunnel is normal (unchanged). In **include** mode, if the tunnel's `DNS` server IP is **not** inside any `SplitRange`, DNS queries won't route through the tunnel → the badge should read this as "DNS bypassing tunnel", which may be intended (split DNS) or a leak.
- 4.0.0 scope: **don't change leak logic**; add a note in the split UI that include-mode users who want tunneled DNS must include their DNS server's IP (or its /32) in the ranges. Revisit auto-including the DNS /32 in a later cycle.

---

## 8. Editor UI

New **"Split tunneling"** section in `TunnelConfigDialog` (the full editor with the Raw tab), laid out so the future Apps list is already positioned:
- **Mode** selector (3 radios or a combo): *Off* · *Exclude these ranges* · *Only these ranges*. Localized (`SplitModeOff/Exclude/Include`).
- **Ranges** — an editable list (one CIDR per line `TextBox`, or add/remove list) validated with the existing CIDR validator on save; inline error like the config validator.
- **Apps (coming in 4.x)** — a **disabled** sub-list with a "Per-app split arrives in a later update" hint, so the final layout ships now. Bound to `SplitApps` but read-only/greyed in 4.0.0.
- Section is **hidden for companion (`Source != "local"`) tunnels** in 4.0.0? — No: AllowedIPs rewrite only works for local tunnels we build the conf for. For companion tunnels we don't own the conf. **Decision:** show the section only when `Source == "local"`; grey with a hint otherwise. (Confirm during implementation.)

`TunnelMetadataDialog` (companion/link editor) does **not** get split controls in 4.0.0 (no conf ownership).

New lang keys ×12 (build trap #3): `SplitSectionHeader`, `SplitSectionHint`, `SplitModeOff`, `SplitModeExclude`, `SplitModeInclude`, `SplitRangesLabel`, `SplitRangesHint`, `SplitAppsLabel`, `SplitAppsComingSoon`, `SplitLocalOnlyHint`, plus any validation strings. Validate each with `JsonDocument.Parse`; keep placeholder integrity.

---

## 9. CLI

- `info <name>` — surface `SplitMode` + `SplitRanges` (and effective AllowedIPs when local). Read-only in 4.0.0.
- `import`/`export` — round-trip split fields (§10). No new connect-time flags in 4.0.0 (`--split` CLI authoring can wait).
- Keep `CliRunner` output helpers (`Info/Ok`) consistent; `--json` includes the split fields.

---

## 10. Export / import round-trip (`TunnelExportService`)

Split config is **portable** (unlike the local-only kill/hide-ring prefs) — it defines the tunnel's routing intent, so it *should* travel with the tunnel.
- Add to `TunnelSettings`: `string? SplitMode`, `List<string>? SplitRanges`. (Hold `SplitApps` until WinDivert to avoid advertising an unused field — or include it now for schema stability; **decision: include `SplitMode` + `SplitRanges` only** in 4.0.0.)
- `BuildSettingsBlock`/`Emit`: `SplitRanges` is a list → emit as a comma-joined value on one `# MasselGUARD-SplitRanges: a/24, b/32` line (no newlines, so no base64 needed); `SplitMode` a plain line.
- `ParseImportText`/`ApplyKey`: parse them back; split the ranges on comma, trim, drop empties.
- Raw tab (`BuildExportText`) shows them like other passthrough settings.

---

## 11. WinDivert groundwork (no dependency added in 4.0.0)

- Reserve `windivert\<arch>\` layout (WinDivert ships x64/arm64 `.dll` + `.sys`), mirroring `wireguard-deps\<arch>\`.
- **Do the §5 licensing groundwork in HANDOVER before adding the binary** — WinDivert is LGPLv3/GPLv3; take the **LGPLv3** path (dynamic-link unmodified `WinDivert.dll`, ship its LICENSE + LGPL/GPL texts, preserve user's ability to replace the DLL), add Basil (basil00) to About credits + a `THIRD-PARTY-NOTICES.md` entry. Plan an AV-allowlisting note (some engines flag `WinDivert.sys`).
- The `SplitApps` field + `ISplitTunnelBackend` per-app hooks are the only schema/abstraction seams WinDivert needs — both ship inert in 4.0.0.

---

## 12. Build traps to respect (from HANDOVER)

1. `Models/StoredTunnel.cs` stays WPF-free (CLI shares it).
2. New shared services (`CidrMath.cs`, `ISplitTunnelBackend.cs`/`RouteBasedBackend.cs`) **must be added to `MasselGUARDcli.csproj`'s explicit `<Compile Include>` list** — the CLI globs Models, not Services.
3. Any new lang key → **all 12** `lang/*.json`, validated with `JsonDocument.Parse`, full parity + `{0}` integrity.
4. User runs `BUILD.bat` + manual-tests; assistant only `dotnet build`-verifies both projects.

---

## 13. Suggested implementation order

1. ✅ **DONE — `CidrMath.cs`** + self-test (exclude/include set math). Pure, no UI. Implemented as `Services/CidrMath.cs` (WPF-free, added to `MasselGUARDcli.csproj`); `ComputeEffectiveAllowedIPs(base, mode, ranges)` is the entry point. Tests run via the hidden CLI command **`MasselGUARDcli selftest`** (`CidrMath.RunSelfTest()` → 18 cases: exact-string for small outputs, property-checked — disjoint-from-excludes + `(base−effective)−excludes==∅` — for large/compound ones). `selftest` bypasses the CLI elevation gate (pure, no driver/config) so it runs in any terminal / CI. **18/18 green; both projects build clean.** **Historical note:** the repo has **no test project** today (only `MasselGUARD.csproj` + `MasselGUARDcli.csproj`). Options: (a) add a minimal `MasselGUARD.Tests` xUnit project (cleanest, but new project + CI consideration); (b) a hidden CLI self-test command (`MasselGUARDcli --selftest cidr`) that asserts a table of cases — no new project, ships nothing user-facing, runs in `dotnet build`+run. **Lean (b)** for 4.0.0 to avoid a test-project/CI setup mid-cycle, unless a test project is wanted long-term. Decide before step 1.
2. ✅ **DONE — Data model.** `StoredTunnel.SplitMode`/`SplitRanges`/`SplitApps` added (defaults keep existing tunnels at "off"); `Models/SplitConfig.cs` DTO with `From(StoredTunnel)` + `HasRouteSplit`/`HasAppSplit`. Both WPF-free, picked up by the CLI Models glob. Both builds clean.
3. ✅ **DONE — `ISplitTunnelBackend` + `RouteBasedBackend`** (`Services/SplitTunnelBackend.cs`, in CLI csproj). `ApplyToConfig` wired into `TunnelService.Connect` right **after decrypt, before validation** (so the rewritten conf is what gets validated + written); no-op unless `SplitConfig.HasRouteSplit`; fails safe (connects with original conf on any error). Backend instance `_splitBackend` on `TunnelService`. End-to-end verified headlessly via `selftest` (11 backend cases: off/empty unchanged, include→AllowedIPs=includes with other lines preserved, exclude drops the block + keeps v6, bypass-range gating). **29/29 green; both builds clean.**
4. ✅ **DONE — Kill-switch bypass.** `KillSwitchService.Enable` gained an optional `bypassRanges` arg; exclude-mode ranges get `MasselGUARD_KS_Allow_Split_<tunnel>_<i>` allow-rules past the global block, tracked by **exact name** in `_splitRuleNames` (tunnel names can prefix one another) and removed precisely in `RemoveTunnelRules`. `CleanupStaleRules` already prefix-scans all `MasselGUARD_KS_` rules so crash-recovery covers them. Fed from `_splitBackend.KillSwitchBypassRanges(split)` at the local-tunnel enable site only (companion tunnels unaffected).
5. ✅ **DONE — Editor UI.** New **Split** tab in `TunnelConfigDialog` (local-tunnel editor only — `TunnelMetadataDialog` untouched): 3 mode radios (Off/Exclude/Include), a ranges `TextBox` (one CIDR per line, enabled only when a mode is active, validated on save via `IsValidCidr` → `SplitInvalidRange` message + tab focus), and a disabled **Apps** box with a "coming in a later 4.x" note. Threaded through `existingSplitMode`/`existingSplitRanges`/`existingSplitApps` ctor args + `ResultSplitMode`/`ResultSplitRanges`, wired in `MainWindow` add/edit (SplitApps preserved untouched on edit). 11 new lang keys ×12 (`TunnelDialogTabSplit`, `SplitSectionHeader`/`Hint`, `SplitMode{Off,Exclude,Include}`, `SplitRanges{Label,Hint}`, `SplitApps{Label,ComingSoon}`, `SplitInvalidRange`) — parity 739, JsonDocument-validated, `{0}` intact. Both builds clean.
6. ✅ **DONE — Export/import round-trip + CLI `info`.** `TunnelExportService.TunnelSettings` carries `SplitMode` + `SplitRanges` (SplitApps deliberately excluded); emitted as readable `# MasselGUARD-SplitMode:` / `# MasselGUARD-SplitRanges: a/24, b/32` comment lines (comma-joined, no base64); `From` skips the off/empty default; `ApplyTo` + `ApplyKey` restore on import. GUI import (`MainWindow`/`ImportTunnelDialog` → `ApplyTo`) and CLI `import` (`--password`/`--group`, `ApplyTo`) restore for free; Raw-tab `ApplySettingsToControls` reflects split into the radios/box. CLI `info` shows `Split: <mode> — <ranges>` (text) + `split_mode`/`split_ranges` (json), gated by `SplitModeActive`. Round-trip covered by 6 new `selftest` cases → **35/35 green**; both builds clean.
7. ✅ **DONE — Docs + version bump.** WHATSNEW (`v4.0.0 — Forking Fox` entry), MANUAL (§5 *Split tunneling*), CLIManual (`info` split fields + non-elevated `help`/`version` note), Reference (§43 technical), CLAUDE.md (design bullet + structure). **4.0.0 cut:** `UpdateChecker.cs` (`CurrentVersion` + codename), `BUILD.bat` (VERSION/CODENAME), both `.csproj` (Version/Assembly/File/Informational), all doc headers + current-version sample outputs. `MasselGUARDcli version` → `v4.0.0 | Forking Fox`. Both builds clean; selftest 35/35. **Remaining (manual/release):** build ARM64 `tunnel.dll` (outstanding from 3.9.0), tag/release `4.0.0` with both arch zips, and **manually test split on a live tunnel**.

Each step is independently `dotnet build`-verifiable; steps 1–4 are testable headless before any XAML.

---

## 14. Open decisions (resolve during implementation)

- **Companion tunnels:** confirm split section is local-only (we don't own companion confs). Likely yes.
- **Multi-peer configs:** 4.0.0 applies split to the first `[Peer]` only — document, or block split when >1 peer? Lean: apply to all peers whose AllowedIPs overlap, but ship single-peer first.
- **IPv6 default in include mode:** if user includes only v4 ranges, should `::/0` be dropped from the tunnel (no v6 through tunnel) — yes, that's the natural result of intersect-with-includes.
- **Export `SplitApps`:** excluded from `TunnelSettings` in 4.0.0 (unused); add when WinDivert lands.
