# DNS Automation — Design (feature-dns-automation)

**Status:** design (no feature code yet). Branch: `feature-dns-automation`. Target: a future 4.x (post-4.1.0 "Layered Lynx"), codename TBD. This document is authoritative; keep it in sync as code lands (mark steps ✅ like `SplitTunneling-Design.md`).

**Headline:** rule-driven **resolver policy** — "on Wi-Fi X, use DNS server Y (optionally encrypted / DoH)" — that works **independently of tunnels**, including when no tunnel is active. Chosen model: **C (hybrid)** — DNS is a first-class policy evaluated on its own axis, in parallel with the tunnel action; any trigger can carry a DNS target; an active tunnel's own DNS supersedes while it's connected.

---

## 1. Where this fits our stack

- The rule pipeline already exists and is the right shape: `WiFiService.SsidChanged` → `MainViewModel.ApplyWifiState` → `RuleEngine.EvaluateWifi` → `ApplyRuleResult`. We add a **parallel** DNS evaluation on the same triggers.
- The identity we need is **already captured**: `WiFiService` reads the WLAN **interface GUID** — exactly the key for per-interface DNS. No new detection surface.
- The app is **always elevated** (`requireAdministrator`), so writing per-interface DNS / DoH settings is permitted without extra elevation (same footing as `DnsLeakService`).
- DNS is applied to the **physical adapter's TCP/IP settings**, not the routing table and not the tunnel — so there is **no kernel/driver surface** and no coupling to wireguard-NT. Like split tunneling, this keeps the feature medium-sized.

The one real coupling is **resolver ownership** (§6): a connected tunnel already sets its own DNS (from the `.conf` `DNS =` line). DNS rules must **defer** to a tunnel that owns resolution, and re-assert when it drops — otherwise the two fight over the adapter.

---

## 2. Model C recap — the parallel DNS axis

Two independent decisions are made on every network change:

1. **Tunnel action** — unchanged: `RuleEngine.EvaluateWifi` → Activate / Disconnect / None.
2. **DNS action** — new: `RuleEngine.EvaluateDns` → ApplyProfile / Automatic (DHCP) / None, using the **same precedence order** and the same triggers.

Cross-cutting invariant: **while a tunnel that carries its own DNS is connected, the tunnel owns the resolver** and the DNS action is held (recorded but not written to the physical NIC). On tunnel disconnect, the pending DNS action is (re)applied to the physical adapter. This is what makes "DNS even with no tunnel" and "DNS composes with tunnels" both true.

---

## 3. Data model

### 3.1 `Models/DnsProfile.cs` (NEW — WPF-free, CLI-shared via the `Models\*.cs` glob)

A named resolver the rules point at. Kept small and string-enum-based to match the codebase convention (`Source`, `AutoReconnectMode`, `TrustedWhen`, `SplitMode` are all string enums).

```csharp
public class DnsProfile : ObservableObject   // ObservableObject for the editor list
{
    /// <summary>Stable id referenced by rules/config (GUID string). Never the display name.</summary>
    public string Id   { get; set; } = System.Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";   // "Cloudflare", "Work", "Quad9"…

    // Plain (Do53) servers. Empty string = unset for that slot.
    public string V4Primary   { get; set; } = "";
    public string V4Secondary { get; set; } = "";
    public string V6Primary   { get; set; } = "";
    public string V6Secondary { get; set; } = "";

    /// <summary>"plain" (Do53 only) | "doh" (DNS-over-HTTPS) | "auto"
    /// (DoH when the OS/servers support it, else plain).</summary>
    public string Encryption  { get; set; } = "plain";

    /// <summary>DoH URI template, e.g. "https://cloudflare-dns.com/dns-query".
    /// Used when Encryption != "plain". One template applies to the profile's servers.</summary>
    public string DohTemplate { get; set; } = "";

    /// <summary>Fail-closed: when true and DoH can't be established, DO NOT fall back to
    /// plain — surface an error instead (privacy-preserving). Default false (best-effort).</summary>
    public bool RequireEncryption { get; set; } = false;

    // Special sentinel profiles resolved by id, not stored:
    //   "__automatic__" = hand DNS back to DHCP (revert to network-provided).
    //   ""              = no DNS action.
}
```

Two built-in virtual ids (never persisted as `DnsProfile`s, resolved in code, mirroring `ThemeManager.__system__`):
- `__automatic__` — set the interface back to **DHCP/automatic** DNS.
- `""` (empty) — **no DNS action** (leave whatever is there).

Ship a small **preset seed** the user can add with one click: Cloudflare (`1.1.1.1`/`1.0.0.1`, DoH `cloudflare-dns.com/dns-query`), Google, Quad9, AdGuard, OpenDNS, NextDNS (template with the user's config id).

### 3.2 `Models/TunnelRule.cs` additions (WPF-free)

DNS is attachable to **any** rule (Model C), plus a DNS-only rule needs no tunnel:

```csharp
/// <summary>Optional DNS profile id this rule applies (in parallel with its tunnel action).
/// "" = the rule makes no DNS change. "__automatic__" = revert to DHCP. A rule may set a
/// DNS profile with an empty Tunnel — that's a DNS-only rule (no tunnel action).</summary>
public string DnsProfileId { get; set; } = "";
```

- No new `Kind` is required: a `wifi`/`trusted`/`schedule` rule with `DnsProfileId` set and `Tunnel` empty is a **DNS-only** rule. `RuleName`/`SsidDisplay` get DNS-aware summaries (e.g. `📶 CAPE → 🌐 Cloudflare`, or `📶 Home → DHCP`).
- A rule with **both** `Tunnel` and `DnsProfileId` set is legal (usually redundant for full tunnels; useful for split).

### 3.3 `Models/AppConfig.cs` additions

```csharp
// ── DNS automation ────────────────────────────────────────────────────
/// <summary>Master switch for the DNS rule engine (separate from ManualMode,
/// which also gates it). Default off so upgrades change nothing until opted in.</summary>
public bool DnsAutomationEnabled { get; set; } = false;

/// <summary>Named resolver profiles (see DnsProfile). Rules reference these by Id.</summary>
public List<DnsProfile> DnsProfiles { get; set; } = new();

/// <summary>DNS profile id applied when no DNS rule matches. "" = leave alone;
/// "__automatic__" = force DHCP everywhere unmatched.</summary>
public string DefaultDnsProfileId { get; set; } = "";

/// <summary>DNS profile applied on an OPEN/unsecured network (parallels OpenWifiTunnel).
/// Lets "any passwordless Wi-Fi → encrypted DoH" work with zero per-SSID rules.</summary>
public string OpenWifiDnsProfileId { get; set; } = "";

/// <summary>Which address families to touch: "both" | "v4" | "v6". Default both
/// (setting only v4 leaves v6 leaking to the network resolver).</summary>
public string DnsAddressFamilies { get; set; } = "both";
```

All defaults preserve today's behavior (engine off, no profiles). `TunnelRule.DnsProfileId` defaults `""` so existing rules are unaffected.

---

## 4. DNS engine — `RuleEngine.EvaluateDns` (pure, GUI-side)

`RuleEngine` is **not** in `MasselGUARDcli.csproj` (the CLI doesn't evaluate rules), so this can use only shared/pure types — which it does. Add alongside `EvaluateWifi`:

```csharp
public enum DnsActionKind { None, Apply, Automatic }
public record DnsResult(DnsActionKind Action, string? ProfileId, string Reason);
public DnsResult EvaluateDns(AppConfig cfg, string? ssid, bool isOpenNetwork);
```

Precedence (first match wins — mirrors `EvaluateWifi` so the two axes are predictable):

1. `ManualMode || !DnsAutomationEnabled` → **None** (all DNS automation off).
2. **Open-network** → `OpenWifiDnsProfileId` if set (isOpenNetwork).
3. **Explicit SSID rule** — an enabled rule (`Kind=="wifi"`) matching the SSID with a non-empty `DnsProfileId`.
4. **Trusted/untrusted rule** — enabled `Kind=="trusted"` rule firing on its side (reuse `TrustedWhenOnList` logic) with a `DnsProfileId`.
5. **Schedule rule** — enabled `Kind=="schedule"` rule currently in-window with a `DnsProfileId` (evaluated on the schedule timer as well as on Wi-Fi change).
6. **Default** — `DefaultDnsProfileId` (`__automatic__` → Automatic, "" → None, else Apply).

Resolving a `ProfileId` of `__automatic__` → `DnsActionKind.Automatic`; `""` → `None`; anything else → `Apply` (with the profile looked up from `cfg.DnsProfiles`; a dangling id → None + a warning log).

`EvaluateWifiDisconnected` gets a DNS sibling: on genuine Wi-Fi disconnect, DNS action = Automatic if a default is set, else None (so we don't strand a resolver on a NIC with no network).

---

## 5. `Services/DnsService.cs` (NEW — GUI-side; per-interface apply/revert)

Owns all the Windows plumbing. **Not** CLI-shared (GUI-only) unless we later add a CLI `dns` command — keep it out of `MasselGUARDcli.csproj` for now.

Responsibilities:
- **Apply** a `DnsProfile` to an interface (by GUID): set v4/v6 servers per `DnsAddressFamilies`; configure DoH when `Encryption != "plain"`.
- **Automatic**: revert the interface to DHCP-provided DNS.
- **Snapshot / restore**: capture the interface's *current* DNS config before the first override so we can restore the user's/corporate original exactly (see §7).
- **Flush**: reuse the `ipconfig /flushdns` pattern from `DnsLeakService` (full System32 path, no PATH hijack).

### Windows APIs (in order of preference)

1. **`SetInterfaceDnsSettings` / `GetInterfaceDnsSettings`** (`iphlpapi.dll`, `DNS_INTERFACE_SETTINGS`) — the modern, supported path. Basic name-server setting: **Win10 2004+**. **DoH** fields (`DNS_INTERFACE_SETTINGS3`, `Do53`/`DoH` policy + auto-template recognition): **Windows 11 21H2+**.
2. **DoH template registration** for custom/unknown resolvers: `netsh dns add encryption server=<ip> dohtemplate=<uri> autoupgrade=yes` (Win11), then set the servers. Windows auto-recognizes well-known resolver IPs (1.1.1.1, 8.8.8.8, 9.9.9.9, …) without manual templates.
3. **Fallback (plain only)** for older Win10: `netsh interface ipv4 set dnsservers name="<alias>" static <ip> primary` (+ `add` for secondary) and `ipv6` equivalents; `set dnsservers ... dhcp` to revert. Or the registry `Tcpip\Parameters\Interfaces\{GUID}\NameServer` + DHCP notify. **Prefer the API**; keep netsh as the compatibility path.

### OS-capability gating
- Detect Windows build once. If DoH isn't supported and a profile requests it:
  - `RequireEncryption == true` → **fail closed**: don't apply plain, surface an error + log ("DoH unavailable on this Windows build").
  - `RequireEncryption == false` → apply plain servers, log a downgrade notice.
- All native calls wrapped; failures are logged and non-fatal (never crash the poll thread), matching `DnsLeakService`'s best-effort posture.

### Interface targeting
- Primary target: the **active Wi-Fi adapter** (GUID from `WiFiService`). `DnsAddressFamilies` decides v4/v6.
- Multiple active NICs (Wi-Fi + Ethernet): v1 targets **the Wi-Fi interface the rule matched on**. Document; revisit "all active" later.

---

## 6. Resolver ownership (tunnel coupling)

Single source of truth: a small **owner** state in `MainViewModel` — `Physical` (DNS rule owns the adapter resolver) vs `Tunnel` (a connected tunnel owns resolution).

- **Tunnel connects** (any tunnel whose `.conf` has a `DNS =`): set owner = Tunnel. **Do not** write the pending DNS profile to the physical NIC (the tunnel already redirected resolution). Keep the pending `DnsResult` recorded.
- **Tunnel disconnects**: owner = Physical → re-run the DNS action for the current network (re-apply profile / automatic).
- **No tunnel, network change**: owner = Physical → apply the `DnsResult` directly. (The headline case.)
- **Rule sets both tunnel + DNS**: the tunnel action runs; the DNS profile is held under Tunnel ownership and only matters if that tunnel is split/has no DNS. (A tunnel *without* a `DNS =` line does **not** take ownership — the DNS rule then applies to the physical NIC even while connected, which is the correct behavior for a split tunnel that deliberately leaves DNS local.)

This keeps the two axes independent while guaranteeing they never write the adapter at the same time.

**Manual override precedence (2026-09-18, revised):** the earlier rule was "an active tunnel's DNS always supersedes." That now has **one exception**: a **manually Enabled DNS profile** (`MainViewModel._manualDnsProfileId` set to a real profile id, via the DNS panel's Enable) outranks *everything*, including a connected tunnel's own DNS — the user explicitly chose that resolver. `ApplyPendingDns` resolves a forced profile **before** the `_tunnelOwnsDns` check and re-asserts it on the tunnel's **rising** edge too (the poll now calls `ApplyPendingDns` on both edges), so the profile wins even if the tunnel sets DNS after connecting. **Revert-to-default** (`ManualRevertToDefault`, 2026-09-19) is a **one-shot restore of the pre-override snapshot**, not a persistent state: it clears `_manualDnsProfileId`/`_pendingDns` and calls `DnsService.Restore(guid)`, so the NIC goes back to *exactly* what it was before MasselGUARD first touched it (e.g. "obtain DNS automatically" ⇒ DHCP, not a forced static server). It runs **regardless of tunnel ownership**, because a manually-enabled profile writes a static resolver to the physical NIC to beat the tunnel — that override must be undone even while connected (the earlier `AutomaticId`-based revert wrongly *held* under tunnel ownership and left the static IP in place). Automatic rule-driven DNS still defers to the tunnel as before — only a *manual* profile overrides it. (Runtime-only; resets to following automation on restart.)

---

## 7. Lifecycle & crash recovery (correctness-critical)

- **Snapshot before first override**: persist the interface's original DNS (servers + DHCP-vs-static + families) to `%APPDATA%\MasselGUARD\dns_state.json`, keyed by interface GUID, **before** the first write. This is the restore source and the crash-recovery record.
- **Leave network / switch SSID**: revert the previous interface to its snapshot (or to the next rule's profile) before applying the new one — never stack overrides.
- **App exit** (`App.xaml.cs` shutdown): **restore every overridden interface** from `dns_state.json`, then clear it. Non-negotiable — closing the app must not strand a resolver (especially an unreachable custom one).
- **App crash / hard kill**: on next startup, if `dns_state.json` shows an interface we overrode and never restored, **restore it immediately** (and log). This mirrors `KillSwitchService.CleanupStaleRules` crash-recovery.
- **Debounce**: reuse the existing 2 s Wi-Fi disconnect debounce so a VPN blip / roam doesn't thrash DNS.
- **Flush** on every apply/revert so already-cached answers from the old resolver are dropped.

---

## 8. Interaction with `DnsLeakService`

- `DnsLeakService` toggles the **global** smart-multi-homed / parallel-A-AAAA leak policies. DNS automation sets **per-interface** servers. They're orthogonal but interact:
  - Setting a **custom LAN-side DNS** on a split tunnel is, by definition, "DNS not going through the tunnel." If the leak policies are on, they may pin resolution to the tunnel and defeat the rule. Document the precedence: **on a split/no-DNS tunnel, DNS rules win on the physical NIC**; on a full tunnel with its own DNS, the tunnel wins (§6).
- v1: **do not change** leak logic. Add a note in the DNS UI. Revisit auto-coordinating the two later.

---

## 9. UI

### 9.1 DNS profiles manager (new)
A small manager (in `SettingsWindow`, a "DNS" page, or a dedicated dialog) to CRUD `DnsProfile`s: name, v4/v6 primary/secondary, encryption (Plain / DoH / Auto), DoH template, "require encryption" checkbox. A **"Add preset"** dropdown seeds Cloudflare/Google/Quad9/AdGuard/OpenDNS/NextDNS. Validate IPs and the DoH URI on save.

### 9.2 DNS on rules (`RuleDialog`)
Add an optional **DNS** selector to the rule editor (a combo of profiles + "— none —" + "Automatic (DHCP)"). A rule may set a tunnel, a DNS profile, or **both**; requiring neither is invalid. Update the rules list to show the DNS target (icon `🌐`).

### 9.3 Settings (DNS page)
- Master **"Enable DNS automation"** toggle (`DnsAutomationEnabled`).
- **Default DNS** (unmatched networks) and **Open-network DNS** selectors.
- **Address families** (Both / IPv4 / IPv6).
- Status line: current effective resolver + owner (Physical/Tunnel), like the DNS-leak badge.

### 9.4 Localization
Every new string → **all 12** `lang/*.json`, validated with `JsonDocument.Parse`, full key parity + `{0}` integrity (build trap). Expect ~20–30 new keys (profile fields, encryption modes, settings labels, statuses, errors).

---

## 10. CLI

- v1 minimal: `MasselGUARDcli dns status` (current per-interface resolver + owner) — read-only, non-elevated-safe like `version`/`selftest`. Applying DNS from the CLI (`dns set`) can wait; if added it must elevate.
- `--json` includes the DNS state.
- Note: `DnsService` stays GUI-side until/unless a CLI apply command is added; only then move it (and gate elevation).

---

## 11. Export / import

- `DnsProfile`s and DNS rules are part of settings → they round-trip through `PresetService` (full-settings snapshot) automatically once in `AppConfig`. Confirm `PresetService.PolicyFields`/`Blocks` include a **"DNS"** block so managed presets can force/lock DNS profiles + the master toggle (kiosk case: "always use corporate DoH, locked").
- Per-tunnel export (`TunnelExportService`) carries a tunnel's config; a rule's `DnsProfileId` travels with **rules** (via `PresetService` "Rules" block), not with a single tunnel — so no `TunnelExportService` change needed unless we decide per-tunnel DNS export later.

---

## 12. Build traps to respect

1. `Models/DnsProfile.cs` and `TunnelRule`/`AppConfig` additions stay **WPF-free** — the CLI globs `Models\*.cs`. (`ObservableObject` lives in `Infrastructure`, already CLI-shared — fine.)
2. The pure DNS precedence lives in `Services/DnsPolicy.cs`, which **is** in `MasselGUARDcli.csproj` (so `selftest` sees it) — keep it WPF-free and side-effect-free. `RuleEngine.EvaluateDns` is a GUI-side wrapper over it.
3. `DnsService.cs` is **GUI-only** for now — do **not** add to `MasselGUARDcli.csproj` unless a CLI `dns set` command is built (then handle elevation).
4. Any new lang key → **all 12** `lang/*.json`, `JsonDocument.Parse`-validated, parity + placeholder integrity.
5. If a new shared service is later extracted, add it to `MasselGUARDcli.csproj`'s explicit `<Compile Include>` list (CLI globs Models, not Services).
6. User runs `BUILD.bat` + manual-tests; assistant only `dotnet build`-verifies both projects. The elevated GUI and live per-interface DNS can't be exercised here — visual/functional checks are the user's.

---

## 13. Suggested implementation order

1. ✅ **DONE — Data model.** `Models/DnsProfile.cs` (ObservableObject, WPF-free: Id/Name/v4+v6 primary+secondary/Encryption/DohTemplate/RequireEncryption; `AutomaticId`/`NoneId` sentinels; `Clone()`; display helpers `IsEncrypted`/`EncryptionDisplay`/`ServersDisplay`; `BuiltInPresets()` seed — Cloudflare ±malware, Google, Quad9, AdGuard, OpenDNS, NextDNS-template). `TunnelRule.DnsProfileId` added (`""` default → existing rules unaffected). `AppConfig`: `DnsAutomationEnabled` (off), `DnsProfiles`, `DefaultDnsProfileId`, `OpenWifiDnsProfileId`, `DnsAddressFamilies` ("both"). All defaults keep current behaviour. **Both projects build clean (0/0).**
2. ✅ **DONE — DNS precedence (`Services/DnsPolicy.cs`, CLI-visible — decision (a)).** Pure `DnsPolicy.Evaluate(cfg, ssid, isOpen, now)` → `DnsResult(DnsActionKind None|Apply|Automatic, ProfileId, Reason)` with the §4 precedence (off → open → SSID → trusted → schedule → default); dangling profile id fails safe to None. `IsDnsOnly(rule)` helper + the canonical `IsWithinSchedule` live here too. Added to `MasselGUARDcli.csproj`. `RuleEngine.EvaluateDns` is a thin wrapper (uses `DateTime.Now`); `RuleEngine.IsWithinSchedule` now delegates to `DnsPolicy` (single impl); `EvaluateWifi`/`EvaluateSchedules` skip DNS-only rules (`IsDnsOnly`) so a DNS-only rule never reads as "disconnect" on the tunnel axis. Self-test wired into `MasselGUARDcli selftest` → **DnsPolicy 19 cases; 54/54 total green**; both builds clean. (Note: the pure evaluator does **not** mutate `ExecutionCount` — counting stays with the tunnel axis / is deferred to the GUI integration in step 4.)
3. ✅ **DONE — `Services/DnsService.cs`** (GUI-only; not in the CLI csproj). Per-interface apply/revert of **plain** IPv4/IPv6, keyed by adapter GUID → alias (`NetworkInterface`), honouring `DnsAddressFamilies`. **Decision: netsh-first** (`interface ip[v4|v6] set/add dnsservers … static/dhcp validate=no`, invoked via full-System32 path + `ArgumentList` so aliases with spaces are safe) rather than `SetInterfaceDnsSettings` — reliable Win10/11 and trivial DHCP revert; the DoH-capable API is introduced in step 6, which needs it anyway. **Snapshot** original static/DHCP DNS (read from the `Tcpip`/`Tcpip6` `NameServer` registry values; empty = DHCP) to `%APPDATA%\MasselGUARD\dns_state.json` before the first override, keyed by GUID; `ApplyProfile`/`SetAutomatic`/`Restore`/`RestoreAll`/`HasOverride` API. `RestoreAll` is what step 5 calls on exit + startup crash-recovery. Encrypted profiles apply their plain servers for now and log "encryption pending". Reuses the `ipconfig /flushdns` pattern. Both builds clean; no self-test (side-effecting — user verifies live against a spare interface).
4. ✅ **DONE — Resolver ownership + integration.** `WiFiService.CurrentInterfaceGuid` now exposes the connected WLAN adapter GUID (captured in `ReadCurrentSsidFromApi`). `MainViewModel` owns a `DnsService`; `ApplyWifiState` (the live path) calls `ApplyDnsForCurrentNetwork(ssid, isOpen)` **alongside** `ApplyRuleResult` — `_pendingDns = _rules.EvaluateDns(...)` then `ApplyPendingDns()`. `ApplyPendingDns` writes to `CurrentInterfaceGuid` honouring `DnsAddressFamilies`: Apply → `ApplyProfile`, Automatic → `SetAutomatic`, None → `Restore` any prior override (hand the network its own resolver back). **Ownership (§6):** a `_tunnelOwnsDns` flag (any tunnel active) is recomputed at the end of `RefreshTunnelStatus`; while set, `ApplyPendingDns` **holds** (tunnel DNS/NRPT supersedes); on the falling edge (tunnel released) it re-asserts the network's DNS. `Dispose` calls `RestoreAll()` so exit never strands an override. Nothing runs by default (engine off → `EvaluateDns` = None → no-op). **Pragmatic v1 note:** ownership = "any tunnel active" (not "tunnel carries a DNS= line") — the split-tunnel-without-DNS nuance from §6 is deferred. Both builds clean; selftest 54/54.
5. ✅ **DONE — Lifecycle/crash recovery** (§7). Startup: `MainViewModel` ctor calls `RecoverDnsFromPreviousRun()` right after creating `DnsService` (before any rule applies) — netsh writes persist across a crash/reboot, so `RestoreAll()` returns any interface recorded in `dns_state.json` to its captured original (logs the count when >0; no-op otherwise). Exit: `_vm.Dispose()` is **not** guaranteed to run, so the authoritative exit-restore is `MainViewModel.RestoreDnsOverrides()` (public, idempotent) called from **`App.OnExit`** alongside `KillSwitchSvc.DisableAll()`/`DisconnectAll()`; `Dispose` also calls it as a belt-and-suspenders. `DnsService.OverrideCount` added for the startup log. Both builds clean; selftest 54/54.
6. ✅ **DONE — DoH (encrypted DNS).** `DnsService.ApplyProfile` now handles encrypted profiles: for `Encryption` "doh"/"auto" it registers a machine-wide DoH template per server IP via `netsh dns add encryption server=<ip> dohtemplate=<uri> autoupgrade=yes udpfallback=<no|yes>` (re-registered cleanly each apply so strictness is current; `RequireEncryption` → `udpfallback=no` = fail-closed), then sets the servers as usual — Windows 11 auto-upgrades a plain `set dnsservers` to DoH once the IP has an autoupgrade template. **OS gating:** per-interface DoH is Win11 22000+ (`OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)`); on an older build a DoH profile that `RequireEncryption` is **refused (fail-closed, no snapshot, no change)**, otherwise it downgrades to plain servers with a log. Well-known resolver IPs (Cloudflare/Google/Quad9/…) carry Windows' built-in templates so registration is only needed for custom ones (e.g. NextDNS); a profile with no `DohTemplate` relies on those built-ins. GUI build clean. **Needs live Win11 verification** (user): confirm queries actually go over DoH (e.g. Cloudflare `/help`, or Wireshark shows no port-53). **Known residue:** custom DoH template registrations are machine-wide and not removed on `Restore` (inert unless that server is set); cleanup can be added later.
7. ✅ **DONE — UI.** On its own Settings **DNS** tab (`PageDns`/`TabBtnDns`, `SettingsTabDns`; `RefreshDnsControls` runs on the "Dns" tab — moved out of the Wifi page 2026-09-17) + the rule dialog. **DNS section** (after Trusted-network): master toggle (`DnsAutomationEnabled`), Default-DNS + Open-network-DNS profile combos (— none — / Automatic (DHCP) / each profile, carried as `ComboBoxItem.Tag`), and an Address-families combo (both/v4/v6). **Profiles manager**: a `ListBox` (Name · servers · encryption) with Add… / Add presets / Edit… / Remove; the editor is a code-built themed dialog `Views/DnsProfileEditor.cs` (name, v4/v6 primary+secondary, encryption Plain/DoH/Auto, DoH template + Require-encryption shown only when encrypting) modelled on RuleDialog's counter dialog — no new XAML window. **Add presets** seeds `DnsProfile.BuiltInPresets()` (skips names already present); **Remove** clears any dangling references (default/open/rules). Handlers persist **directly to the live config + Save** (guarded by `_dnsLoading`; the page's draft/apply flow is field-by-field and doesn't touch DNS). **Rule dialog** gained an optional DNS-profile combo (`ResultDnsProfileId`), threaded through `MainWindow` add/edit (`DnsProfileChoices()`); a DNS-only rule = pick a profile, leave the tunnel blank. **Localization:** 36 new keys × 12 files (parity kept); **fully translated into all 11 non-English languages (2026-09-17).** Both builds clean; selftest 54/54. **Needs the user's visual pass on the elevated GUI** (layout, combos, the code-built editor).
8. ✅ **DONE — CLI + docs + version bump.** CLI `dns status` (read-only, non-elevated — added to `IsNonElevatedCommand`; text + `--json`; shows config + live per-interface resolvers via `NetworkInterface`) + help line. Docs: WHATSNEW `v4.2.0 — Resolving Raven` entry, MANUAL §11 "DNS automation" subsection, CLIManual `dns` section, Reference §44 (+ TOC 42–44), CLAUDE.md (design bullet, structure `DnsPolicy`/`DnsService`, CLI table, `IsNonElevatedCommand`). **Version bump 4.1.0 → 4.2.0 "Resolving Raven":** `UpdateChecker.cs` (`CurrentVersion` + `_codenames`), `BUILD.bat` (VERSION/CODENAME), both `.csproj` (Version/Assembly/File), all doc headers. `MasselGUARDcli version` → `v4.2.0 | Resolving Raven`. Both builds clean; selftest 54/54. **Export/import:** `DnsProfile`s + rule `DnsProfileId` + the `AppConfig` DNS fields ride the `PresetService` full-settings snapshot for free (no `TunnelExportService` change); a dedicated managed-preset **DNS lock block** is left for a follow-up.

**Charts — active-DNS band (2026-09-18):** DNS-profile activations are recorded to `%APPDATA%\MasselGUARD\dns_history.json` (`Models/DnsHistoryEntry`, `HistoryService.RecordDnsActivate`/`RecordDnsDeactivate`, gated by `AppConfig.StoreDnsHistory`) from `MainViewModel.ApplyPendingDns` (profile name on Apply; the interface's **actual resolver IP** — e.g. 1.1.1.1 — on Automatic/None via `RecordDnsDefaultResolver`/`CurrentInterfaceResolver`, so the band always shows the real server even with no profile; deactivate only while a tunnel owns DNS).

**Charts — standalone DNS pane + pills (2026-09-18, revised):** the info panel is now **three independent stackable panes** — Timeline, Data-usage, and **DNS** — each with its own header ToggleButton (`ModeTimelineBtn`/`ModeUsageBtn`/`ModeDnsBtn` → `AppConfig.ShowTimelinePane`/`ShowUsagePane`/`ShowDnsPane`). `RenderChart` frames each shown pane as its own card (border+bg+gap) when ≥2 are visible and dispatches to per-pane renderers. The DNS band is no longer drawn inside the timeline/usage canvases (`DrawDnsBand` removed); instead `RenderDnsChart(DnsPaneCanvas)` draws a self-contained pane — its own vertical grid + time-axis, one combined activation band (colour per name via `_dnsColorIndex`, hideable via `_hiddenDnsNames`, inline labels + per-segment tooltips), and **DNS profile pills** (legend chips) into `DnsLegendPanel` (`BuildLegendChip`, click to hide/show). Content gate mirrors the timeline: `StoreDnsHistory && ShowDnsInChart` (Settings → History Capture-DNS + Show-DNS toggles); the header `ShowDnsPane` toggle separately controls pane visibility. `ApplyUsageWindowHeight` adds a small `DnsExtraHeight` so the third pane doesn't squeeze the tunnel list. **DNS-on-hover:** the timeline, data-usage bucket, and ◀▶ nav tooltips all append a DNS row naming the profile/resolver active at that time (`AddDnsRow`/`GetDnsAt`, mirroring `AddWifiRow`/`GetSsidAt`; gated on `StoreDnsHistory` like the WiFi row). **WiFi-rules "Tunnel" column → "Tunnel / DNS"** (`RuleColTunnel` renamed in all 12 langs): the cell now shows the tunnel (🔒) and/or the resolved DNS-profile name (🌐), either or both; a DNS-only rule shows "—" in the Action column instead of "Disconnect". Both builds clean; parity 750×12.

**DNS panel restyle + per-row Enable + separate WiFi DNS column (2026-09-19):** the DNS-profiles panel dropped its plain `GridView` for the **same custom header+ListView pattern as Tunnels/WiFi rules** — a `DnsColGrid` header (Name · Type · Server · Rules · Enable) with `GridSplitter`s for **draggable widths** (persisted via `AppConfig.DnsCol*W` + VM `DnsColNW` proxies + `ColProxy`/`PixelGL`, restored/reset like the others) and **clickable sort headers** (`_dnsSortCol`/`_dnsSortAsc`, `SortDnsRows`, ▲/▼ arrows). The standalone Enable/Disable toolbar button is gone; each row now has an inline **Enable/Disable button** in the new Enable column (`DnsRowEnable_Click`) — enabling one replaces any other (single active profile, marked with a green ● before the name and a Success-coloured button label). The **WiFi-rules table split the combined Tunnel/DNS column into two**: a Tunnel column (🔒, `TunnelColName`) and a separate **DNS column** (🌐, `SettingsFeatureDnsLabel`), both draggable + sortable (`WifColDef5`/`WifCol5W`/`WifiColDnsW`, `RuleSort_Dns`); saved 5-column widths from before are discarded on load. `RuleColTunnel` reverted to plain "Tunnel" (now unused). Both builds clean; selftest 55; parity 806×12.

**Follow-up polish (2026-09-19):** (1) manual Enable/Disable and Revert are **runtime-only** — they no longer create or edit a WiFi rule (`SetCurrentWifiRuleDns` removed); (2) the DNS table header gained an **"Action"** label (`RuleColAction`) over the per-row Enable button column; (3) **feature-gated WiFi columns** — `ApplyWifiColVisibility` drops the Tunnel column when `EnableTunnels` is off and the DNS column when `EnableDns` is off (header button + splitter hidden, column + row cell collapsed to 0; `WifiColGrid_SizeChanged`/`LayoutUpdated` skip hidden columns); (4) the info panel gained a localized **"Charts:"** label (`ChartsLabel`, 12 langs) before the Timeline/Data-usage/DNS toggles; (5) `ApplyUsageWindowHeight` now reserves band + stacked-card overhead for the DNS pane (`DnsExtraHeight` 46→60 + 24 when stacked) so adding DNS as a 2nd/3rd chart no longer clips the WiFi-rules buttons row. Build clean; parity 807×12.

**Diagnostics / Tester (Advanced, 2026-09-21):** `Services/DiagnosticsService.cs` (GUI-only) + a Settings → Advanced panel (checkboxes **WireGuard (local)** / **DNS profiles**, Run/Copy/Clear, monospace debug log). It runs **live**: the WireGuard test picks an available local tunnel (`vm.TunnelList`, `IsLocal && IsAvailable`), connects it via `TunnelEntryViewModel.ConnectAsync`, polls `TunnelDll.IsRunning` + `GetStats` for a recent **handshake**, then disconnects (via `TunnelService.Disconnect`, marking it intentional) unless it was already up. The DNS test creates **one throwaway test profile** (Cloudflare 1.1.1.1 — not every configured profile), applies it to the active interface, reads `NetworkInterface.DnsAddresses` back, verifies, and restores. With both selected it also runs a **tunnel + DNS** combo (apply the test profile while the tunnel is up, confirming a manual profile overrides the tunnel's DNS, then restore). Companion (WireGuard-for-Windows) mode is intentionally not tested. `RunAsync` is awaited on the UI thread (it drives the view-models) and offloads blocking netsh/service work with `Task.Run`; the sink marshals each line via the dispatcher. Parity 816×12.

**Remaining (user / release):** **live testing** on the elevated GUI + a Win11 box (netsh apply/restore against a spare interface, DoH actually engaging, the whole Settings-Wifi UI); decide whether 4.1.0 needs its own WHATSNEW entry (currently absent — 4.2.0 sits above 4.0.0); optional managed-preset DNS-lock block; then merge `feature-dns-automation` → `dev` and, at release, refresh the Scoop bucket + build both-arch zips.

Steps 1–3 are independently `dotnet build`-verifiable and testable before any XAML.

---

## 14. Open decisions (resolve during implementation)

- ✅ **RESOLVED — Selftest home for `EvaluateDns`**: decision (a-variant) — the pure precedence lives in the CLI-compiled `Services/DnsPolicy.cs`; `selftest` covers it (19 cases). `RuleEngine.EvaluateDns` wraps it.
- ✅ **RESOLVED — DoH OS floor**: per-interface DoH requires **Win11 22000+** (`IsDohSupported`); plain DNS via netsh works on any supported build. On older builds a DoH profile that `RequireEncryption` is refused (fail-closed); otherwise it downgrades to plain with a log. (Step 7 UI should surface this in the profile editor.)
- **Multiple active NICs**: v1 targets the matched Wi-Fi interface only — confirm we don't also set Ethernet.
- **IPv6 default**: if a profile sets only v4, do we force v6 to Automatic/off to prevent v6 leak, or leave v6 untouched? Lean: leave untouched but warn (documented under `DnsAddressFamilies`).
- **Interaction with corporate/GPO DNS**: detect and refuse-with-notice vs override-and-restore. Lean: snapshot + restore, but surface when the OS reverts our write (GPO re-applies).
- **Preset NextDNS**: needs a per-user config id in the DoH template — ship as a template with a placeholder the user fills in.
- **Managed-preset lock granularity**: lock the whole DNS block vs individual profiles/toggle.
