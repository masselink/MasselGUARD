# Feature Modules — Design (feature-dns-automation)

**Status:** design + step 1 done. Branch: `feature-dns-automation` (ships with DNS automation, 4.2.0 "Resolving Raven"). This document is authoritative; mark steps ✅ as they land.

**Headline:** make **WireGuard tunnels** and **DNS automation** two independent modules the user turns on/off — in the setup wizard and in Settings — so MasselGUARD can run as a full tunnel manager, a **DNS-only** resolver switcher, or both. Decisions taken: **full modular both ways** (DNS-only hides *all* tunnel UI; tunnels-only hides the DNS section); **≥1 module always on**; **upgraders get both on** (nothing disappears; DNS automation itself stays off until enabled).

---

## 1. Why this is asymmetric

- **DNS is already a self-contained module** — one Settings section, its own engine (`DnsPolicy`/`DnsService`), gated by `DnsAutomationEnabled`. Hiding it entirely is small.
- **Tunnels are the app's core**, woven through: the tunnel list + toolbar (add/connect/quick-connect), groups, the tunnel-rules column, kill switch, auto-reconnect, split tunneling, data caps, the Standalone/Companion **Mode**, the timeline's tunnel bars, and the tunnel picker in rules. A real DNS-only mode gates **all** of it.
- Enablers already present: WiFi detection is tunnel-independent; **DNS-only rules already exist** (`DnsPolicy.IsDnsOnly` — trigger → DNS, no tunnel); the wizard already has a **Mode** step and the app an `AppMode` (Standalone/Companion/Mixed) to extend rather than invent.

---

## 2. Data model (`Models/AppConfig.cs`, WPF-free)

```csharp
// ── Feature modules ───────────────────────────────────────────────────
/// <summary>Show/run the WireGuard tunnel feature (list, connect, kill switch,
/// auto-reconnect, split, caps, Mode, tunnel rules/timeline). Default true.</summary>
public bool EnableTunnels { get; set; } = true;

/// <summary>Show the DNS-automation feature (Settings section + rule DNS pickers +
/// the DNS engine). Default true so upgraders see it; DnsAutomationEnabled is the
/// runtime on/off within it. Default true.</summary>
public bool EnableDns { get; set; } = true;
```

- **Invariant — at least one on.** A single guard `EnsureAtLeastOneModule()` (called by the setter path / on load): if both end up false, force `EnableTunnels = true`. The wizard + settings UI also refuse to uncheck the last-enabled module.
- **Upgrade default = both true** — the property defaults are `true`, and a config deserialized from an older version (no keys) gets both true automatically. No migration code needed; existing users keep tunnels and gain the (still-off) DNS section.
- **Relationship to `AppMode`** — `AppMode` (Standalone/Companion/Mixed) is only meaningful when `EnableTunnels`. In DNS-only mode it's ignored (and its wizard sub-step is skipped). Leave `AppMode` as-is; just gate its UI.
- **Relationship to `DnsAutomationEnabled`** — unchanged. `EnableDns` = "is the DNS feature part of my app"; `DnsAutomationEnabled` = "is DNS automation currently running". DNS never runs when `!EnableDns`.

---

## 3. Central gating (one place, not scattered)

Add a single `MainWindow.ApplyFeatureVisibility()` (called from `OnLoaded` and whenever the toggles change) that flips the visibility of the tunnel surfaces and the DNS surfaces from the two flags. Prefer **binding to VM properties** over imperative `.Visibility =` where the control already binds, to stay MVVM-consistent.

New VM/computed visibilities (on `MainViewModel`, `[JsonIgnore]`):
- `TunnelsVisibility` = `EnableTunnels ? Visible : Collapsed`
- `DnsFeatureEnabled` (bool) for the Settings/rule gating.

### Surfaces hidden when `!EnableTunnels` (DNS-only)
- **Main window:** the entire tunnel list panel + its toolbar (Add / Connect / Quick Connect / Defaults / Export), the group tabs, the per-tunnel rules column. The window centres on the WiFi state + rules + (optionally) a slimmer info panel.
- **Info panel / timeline:** the tunnel bar is empty in DNS-only; show only the WiFi band (and keep the Data-usage pane hidden, since it's per-tunnel). Simplest: hide the whole info section's tunnel/usage bits, keep WiFi. (Refine in the timeline step.)
- **Settings:** the **Tunnels** page/tab, the **Mode** controls, kill switch, auto-reconnect, split, the cap-indicator card (Appearance), the WiFi page's *Default action → Activate tunnel* + *Open-network **tunnel*** (the DNS equivalents stay).
- **Rules:** the rule dialog drops the **tunnel** picker + the trusted-rule tunnel; a rule is *trigger → DNS profile*. The tunnel axis (`EvaluateWifi`) is short-circuited to `None` when `!EnableTunnels` so a stray stored tunnel never activates.
- **Tray / status / About:** hide active-tunnel status line; tray menu drops tunnel connect items.

### Surfaces hidden when `!EnableDns` (tunnels-only)
- The **DNS automation** section on Settings → Wifi (whole block).
- The **DNS** picker in the rule dialog.
- CLI `dns status` still works (read-only) — harmless; or print "DNS feature disabled".
- The DNS engine is skipped in `ApplyWifiState` (guard on `EnableDns`).

---

## 4. Rule engine

- `RuleEngine.EvaluateWifi` / `EvaluateSchedules`: when `!cfg.EnableTunnels`, return `None` (no tunnel action) — DNS-only installs never drive tunnels even if a rule carries one.
- `DnsPolicy.Evaluate`: already gated by `DnsAutomationEnabled`; add nothing, but `MainViewModel.ApplyDnsForCurrentNetwork` should early-out when `!EnableDns`.
- Both guards are cheap and keep a mixed/downgraded config safe.

---

## 5. Wizard (`Views/WizardWindow`)

- **Step 4 "Mode" → "What do you want to use MasselGUARD for?"**: three choices — *WireGuard tunnels* / *DNS automation* / *Both* — writing `EnableTunnels`/`EnableDns`.
- The existing **Standalone/Companion/Mixed** selection becomes a **sub-step shown only when tunnels are enabled** (reuse the current Step 4 UI, moved behind the feature choice or as Step 4b).
- **Step 6 "WiFi Automation"** stays for both (rules exist in both modes); its copy adapts (tunnel-or-DNS).
- Skip tunnel-only steps (e.g. install/DLL bits that assume tunnels) when DNS-only — audit each of steps 5/7/8.
- Keep the wizard's total-step logic flexible (it's a fixed 9 today; feature choice may skip one).

---

## 6. Settings (`Views/SettingsWindow`)

- **General page → "Features"** section: two toggles (Tunnels, DNS), each with a one-line description; the last-enabled one can't be unticked (disable the toggle + a hint). Changing a toggle calls `ApplyFeatureVisibility()` live and re-runs `ShowTab` so the affected pages/sections update immediately.
- Hide the **Tunnels** tab button entirely when `!EnableTunnels`; the DNS section when `!EnableDns`.

---

## 7. Localization

New keys ×12 (English first, translations pre-release): wizard feature-choice step (title + 3 options + descriptions), Settings "Features" section (header + 2 labels + 2 descriptions + last-module hint). ~12–15 keys. Validate parity + `{0}` integrity.

---

## 8. Edge cases

- **Both off** — impossible by invariant; UI blocks it and load forces tunnels on.
- **DNS-only + a config that has tunnels** — tunnels are hidden but not deleted; re-enabling the module shows them again. `EvaluateWifi` returns None so they never activate meanwhile.
- **Tunnels-only + DNS profiles defined** — kept in config, just hidden; DNS engine skipped.
- **CLI in DNS-only** — tunnel commands still function if invoked directly (headless power-user); document that the module flags gate the **GUI**, not the CLI. (Could gate later.)
- **Managed preset / kiosk** — a locked preset could force a module set; fold `EnableTunnels`/`EnableDns` into a `PresetService` "Features" block later (not v1).

---

## 9. Build traps

1. `AppConfig` additions stay WPF-free (CLI globs Models).
2. New lang keys → all 12, parity + placeholder integrity.
3. User runs `BUILD.bat` + live-tests; assistant only `dotnet build`-verifies. The gating especially needs the user's visual pass in both modes.

---

## 10. Suggested implementation order

1. ✅ **DONE — Data model.** `AppConfig.EnableTunnels`/`EnableDns` (both default true) + `EnsureAtLeastOneModule()` invariant (called at end of `ConfigService.Load`). WPF-free; upgraders get both true. Both builds clean.
2. ✅ **DONE — Rule-engine guards.** Tunnel axis: `RuleEngine.EvaluateWifi`/`EvaluateWifiDisconnected`/`EvaluateSchedules` return `None` when `!cfg.EnableTunnels`, so a DNS-only install never activates a stored rule's tunnel. DNS axis: `DnsPolicy.Evaluate` now also gates on `!cfg.EnableDns` (alongside ManualMode / DnsAutomationEnabled), so a tunnels-only install never touches DNS — and because that returns `None`, `ApplyPendingDns` restores any prior override (disabling the DNS module hands the resolver back). New selftest case `dns-module-off`. Both builds clean; **selftest 55/55 (DnsPolicy 20).**
3. ✅ **DONE — Settings "Features" section** (top of General): two `ToggleSwitch`es (`FeatureTunnelsToggle`/`FeatureDnsToggle`) + info card. `RefreshFeatureControls` seeds them and **disables whichever is the sole enabled module** (last-module guard — can't be turned off). `FeatureModule_Changed` persists directly to config + Save, then `ApplyFeatureTabVisibility()` (hides `TabBtnTunnels` when `!EnableTunnels`, `TabBtnDns` when `!EnableDns`, and bounces the active tab to General if it just got hidden) and `MainWindow.ApplyFeatureVisibility()` (Step-3 hook: restores DNS overrides immediately when the DNS module is switched off; tunnel-surface gating is Steps 4–5). Loaded handler applies tab visibility + redirects a hidden `InitialTab` to General. 6 lang keys ×12 (parity **793/793**). GUI builds clean. **Needs the user's visual pass** (toggles, tab show/hide, last-module lock).
4. ✅ **DONE — Tunnels-only gating (hide DNS).** The DNS **tab** already hides via step 3 (`ApplyFeatureTabVisibility`). The remaining DNS surface — the **rule dialog's DNS picker** — is now wrapped in `DnsPickerPanel` and collapsed via a new `RuleDialog` ctor arg `dnsEnabled` (passed `ConfigSvc.Config.EnableDns` from both `MainWindow` add/edit sites). The DNS engine is already skipped when `!EnableDns` (step 2). GUI builds clean. **All 43 new DNS + feature-module lang keys are now fully translated across the 11 non-English languages** (438 strings applied; shared technical tokens like `IPv4 + IPv6`, `DNS-over-HTTPS (DoH)`, the `DNS` tab label kept as-is), parity **793/793 × 12**.
5. 🟡 **MOSTLY DONE — DNS-only gating (hide tunnel surfaces).** Core done & builds clean:
   - **Main window** (`MainWindow.ApplyFeatureVisibility`, called from `OnLoaded` + on toggle): hides `TunnelsHeaderContent` (TUNNELS label/count/tabs), `TunnelBoxBorder` (the tunnel list), `TunnelButtonsPanel` (Add/Edit/Import/Defaults/Export/Delete), and zeroes `TunnelsListRow` (was `3*`/MinHeight 80 → 0) so the WiFi-rules section fills the pane. The ☰ log button (same header grid, other column) and the whole WiFi-rules section stay — rules still drive DNS.
   - **Rule dialog**: `TunnelPickerPanel` hidden via a new `tunnelsEnabled` ctor arg (passed `EnableTunnels` from both MainWindow sites); a DNS-only install's rule is trigger → DNS (empty tunnel). Tunnel axis already short-circuited in step 2.
   - Top bar `QuickConnectBtn`/`TunnelLabel` were already collapsed by default — no change needed.
   **Remaining after step 6:** only the tray menu's tunnel items (minor) — everything else is now gated (see step 6).
6. ✅ **DONE — Settings tunnel-only sections + WiFi-only timeline.** Wrapped the Settings **Wifi** page's tunnel-only sections (`WifiTunnelOnlySections` = Default-action + Open-network tunnel) and the **Appearance** cap-indicator card (`CapIndicatorSection`) and hid them in DNS-only via `ApplyFeatureSectionVisibility()` (called on the Wifi/Appearance tabs). **Info panel:** `MainWindow.ApplyFeatureVisibility` hides the **Data-usage** toggle (`ModeUsageBtn`, per-tunnel) and forces it off in DNS-only, leaving the **WiFi-only timeline** (the tunnel bar renders empty; the WiFi band renders regardless of tunnels). Combined-traffic + session-nav auto-hide with no active tunnels. GUI builds clean. **Remaining:** tray menu tunnel items (minor); the info-panel header still shows the "TIMELINE/DATA USAGE" label pair (now just TIMELINE) — acceptable.
7. ✅ **DONE (main-window DNS panel — redirected from the wizard on 2026-09-17; reworked same day).** A **DNS PROFILES** panel in the right column, **row-aligned with the Tunnels section**: header (row 0, `SharedSizeGroup="HeaderRow"` → same height as TUNNELS; DNS has no group tabs), list (row 1), and an **Add / Edit / Delete / Add presets / Import / Export** button row (row 2) — mirroring the tunnel toolbar. The **Activity log** moved to the WiFi-rules rows (3-5). The `ListView`/`GridView` binds `MainWindow.DnsProfileRow`: **Name · Type · Server · Rules** — Type auto-detected (`DnsProfile.TypeDisplay`: DoH/DoT/DoQ/DNSCrypt from the template scheme, else Plain/DoH/Auto); Server = `DnsProfile.ConnectionDisplay` (DoH/DoT URL if set, else IPv4/IPv6); Rules = WiFi rules referencing it. Header count badge; double-click a row → **Settings → DNS**; Add/Edit reuse `DnsProfileEditor`; Delete confirms + clears references; Import/Export are a JSON profiles file.
   **Layout is centralized in `MainWindow.UpdateContentLayout()`** (called by both `ApplyFeatureVisibility` and `SetLogPanelVisible`): left = Tunnels(0-2)/WiFi-rules(3-5), right = DNS(0-2)/Log(3-5). **Log-collapse now widens only the WiFi-rules line** (WiFi-rules `ColumnSpan=3`) while the DNS panel + Tunnels stay; the right column stays for the DNS panel (only collapses fully when DNS is off, where the log spans rows 0-5). DNS-only spans the DNS panel across the full top width. 7 new lang keys ×12 (parity **800/800**, translated). GUI builds clean. **Needs the user's visual pass** (row alignment DNS↔Tunnels, GridView column widths, log-collapse behaviour).
   **Refinements (2026-09-18):** fixed Edit/Delete/Enable no-op (the `DnsProfileRow` projection now carries `Id`); added **Enable / Disable** buttons that manually apply/clear a profile on the current interface (`MainViewModel.ManualApplyDns`/`ManualRevertDns`); widened the window (1200→1440, MinWidth 980→1120) and enlarged the top row (`TunnelsListRow` 3\*→5\*); and moved the ☰ log-reopen button from the tunnel header to the **WiFi-rules header** (right-aligned, in Column 0 so it stays visible even when the right column collapses). **Single-section layout:** when only one of Tunnels/DNS is active, the Activity log spans the **full right column** again (rows 0-5); it's bottom-only (rows 3-5) only when **both** are on. And in **DNS-only** the DNS panel moves to the **left** column (where tunnels was), leaving the whole right column for the log.
   **Manual override (2026-09-18):** the DNS panel's **Enable** now sets a sticky runtime override (`MainViewModel._manualDnsProfileId`) that **outranks the rule engine** and survives network changes (an active tunnel's DNS still wins). **Disable** clears it (automation resumes / original restored); **Revert to default** forces the system/DHCP default (sticky, `DnsProfile.AutomaticId`). `ApplyPendingDns` checks the manual override before the rule result; the manually-active profile is marked with a ● in the panel. Runtime-only (resets to automation on restart).
   **Button consolidation + Enable→rule (2026-09-18):** the panel row is now **[Enable/Disable toggle] [Revert to default] | [Add] [Edit] [Remove] [More▾]** — the toggle changes state from the selected row (● = active), and **More▾** is a click-opened dropdown (ContextMenu) with Presets / Import / Export. **Enable now also writes "the WiFi's DNS action"**: `SetCurrentWifiRuleDns(profileId)` sets the DNS on the WiFi rule matching the current SSID (creating a DNS-only rule SSID→profile if none exists), so it persists via automation; Disable / Revert-to-default clear it (dropping an auto-created rule that becomes empty). No current SSID → the rule step is a no-op (runtime override still applies).
   **Still TODO — the actual wizard step:** a "What do you want to use MasselGUARD for?" step writing `EnableTunnels`/`EnableDns`, the Standalone/Companion Mode sub-step shown only when tunnels are chosen, and skipping tunnel-only wizard steps in DNS-only. (Deferred; the feature is fully usable via Settings → General → Features.)
8. **Lang ×12, docs** (MANUAL/Reference/CLAUDE.md), and fold into WHATSNEW 4.2.0 (or a later version if this ships separately).

Steps 1–4 are low-risk and independently verifiable; step 5 is the bulk and needs the user's visual testing in DNS-only mode.

---

## 11. Open decisions

- **Timeline in DNS-only**: hide entirely vs WiFi-only band. Lean WiFi-only (it's still useful).
- **Does DNS-only change the window's default size/layout?** The tunnel list is the main pane; without it the window may want a more compact default. Decide during step 5.
- **CLI gating**: leave CLI ungated (flags are GUI-scoped) vs honour the flags. Lean leave ungated for v1.
- **Ship vehicle**: fold into 4.2.0 "Resolving Raven" (same branch) or a follow-up minor. Lean: same branch, since it complements DNS-only use.
