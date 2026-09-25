# Settings & Wizard Restructure - Plan

Status: **structure implemented + localized** (tab renames/merges + feature-gating stubs +
translations for all new/renamed labels, feature stubs, wizard content and title-bar tooltips
across the 12 `lang/*.json`, this pass). The in-tab Basic/Advanced expanders and the
settings-search box (migration steps 4–5) are still deferred. Goal: a settings
layout that stays user-friendly as more settings are added, and a wizard that is a curated subset
of the same structure.

## Implemented layout (this pass)

Sidebar order: **General · WireGuard · DNS · Automation · Appearance · Notifications & history ·
Startup · Diagnostics · About**. The old **Advanced** tab is dissolved; **Wifi** is relabelled
**Automation**; **Tunnels** is relabelled **WireGuard**; **History** is relabelled **Notifications
& history**. Where things landed:

- **General** - language, feature modules (VPN/DNS/Both), view preset, and Import/Export & preset
  (moved in from Advanced). Startup & exit moved out to the Startup tab.
- **WireGuard** / **DNS** - always-visible tabs; when the module is off they show an "enable this
  feature" stub (owner's choice over hiding). DNS also absorbed the DNS leak-protection card.
- **Automation** - unchanged WiFi-rules / default-action / trusted / display cards, relabelled.
- **Appearance** - theme, fonts, cap-indicator, title-bar section buttons. Notifications moved out.
- **Notifications & history** - tray-popup + duration (moved in from Appearance) on top, then the
  existing capture/show, chart options, history list and log settings.
- **Startup** - start-with-Windows / start-minimized / confirm-on-exit (from General) + the
  Installation card (from Advanced).
- **Diagnostics** - tester + system diagnostics + the Theme repository box (from Advanced).

Code notes: `SettingsWindow.ShowTab` gained a `Startup` branch and dropped `Advanced`; feature
stubs are driven by `ApplyFeatureStubs()`/`ApplyFeatureTabVisibility()` (tabs no longer hide);
`PopulateNotifSettings()` populates the relocated notification controls; the stub "Enable" buttons
call `EnableTunnelsFromStub_Click` / `EnableDnsFromStub_Click`. All control `x:Name`s and handlers
were kept stable so `CommitDraft` / `ApplyPresetLocks` are unchanged.

---


## The core question: feature-based vs usage-based?

**Recommendation: hybrid - feature-based top level, with cross-cutting concerns as their own tabs.**

Reasoning:

- The user's first decision (in the wizard) is already **feature-based**: *WireGuard VPN / DNS / Both*
  (`EnableTunnels` / `EnableDns`). That mental model should carry through Settings.
- Feature modules already gate visibility, so a **whole feature tab can hide** when its module is off
  (a DNS-only user never sees WireGuard settings, and vice-versa). Usage-based grouping can't do that.
- Some concerns are **not** feature-specific (Appearance, Notifications, Startup, Diagnostics, About).
  Forcing them under a feature is confusing; they get their own tabs.
- One concern is genuinely **shared**: WiFi rules / automation drive *both* tunnels and DNS. It gets its
  own "Automation" tab rather than being duplicated.

Pure usage-based grouping ("Visibility", "Behaviour", "Data") was considered and rejected: users think
"I want to change my VPN reconnect setting", not "I want to change a behaviour setting" - they'd hunt
across tabs. Usage is better as a *secondary* axis (a Basic/Advanced split inside a tab), not the top level.

## Proposed top-level tabs (stable set, ~8)

| Tab | Contains | Hides when |
|---|---|---|
| **General** | Language, feature modules (VPN/DNS/Both), import/export & preset, run wizard | never |
| **WireGuard** | Connection behaviour (auto-reconnect, kill switch, config validation), data caps defaults, split defaults | `!EnableTunnels` |
| **DNS** | DNS automation on/off, profiles manager, default/open profile, address families, DoH, leak protection | `!EnableDns` |
| **Automation** | WiFi rules, default action, trusted networks, schedules (shared by tunnels + DNS) | `ManualMode` collapses it to a hint |
| **Appearance** | Theme + mode, fonts, cap-indicator style, **title-bar section buttons**, **DNS badge** | never |
| **Notifications & history** | Tray toasts + duration, log level + persistence (clear-on-start, max size), history capture (connections / WiFi / DNS) | never |
| **Startup** | Install/uninstall, start with Windows, start minimized, confirm on exit | never |
| **Diagnostics** | Tester (local/DNS/CLI), System diagnostics, DLL status, theme repo | never |
| **About** | Version, update check, credits | never |

Notes vs. today: **Advanced** is dissolved (its items move to Diagnostics / General / Notifications);
**History** merges into **Notifications & history**; a new **Automation** tab absorbs the WiFi/rules
content that is currently split between the Wifi and Tunnels tabs.

## Keeping it user-friendly as settings grow

1. **Two levels only.** Top tabs (stable, rarely added to) + cards/sections inside. New settings become
   a new *card* in an existing tab, not a new tab. Adding tabs is the thing that erodes usability.
2. **Basic vs Advanced within a tab.** Each tab shows the common settings; rare/power settings live under
   a collapsible "Advanced" expander at the bottom. This is the usage axis, applied *locally*.
3. **Feature-gating (already in place).** Hide entire feature tabs when the module is off. Fewer visible
   settings = friendlier. Extend the same gating to cards (e.g. kill-switch card only if tunnels on).
4. **Settings search.** The highest-leverage long-term move: a search box that filters settings across all
   tabs by label/description. Implementation: tag each settings card with its keywords; filter the visible
   cards live. This makes tab placement much less critical as the count grows.
5. **One placement rule.** A setting lives where its *concept* lives, once. Cross-cutting UI switches
   (title-bar buttons) go in Appearance; behaviour of a feature goes in that feature's tab. Never duplicate
   a control across tabs (mirror the *wizard*, not other tabs).
6. **Consistent card anatomy.** Header (section), one-line muted description, control on the right. Toggles
   for booleans, pills for small enums, combos for long lists. Keeps scanning predictable.

## Wizard = curated subset of the same structure

Principle: **everything in the wizard must also be in Settings; not everything in Settings needs to be in
the wizard.** The wizard is the first-run "happy path": one step per settings tab, showing only that tab's
*most common* decisions, in a sensible order.

Current 9-step wizard already maps cleanly:

| Wizard step | Settings tab it previews |
|---|---|
| Welcome + import + language | General |
| Feature choice (VPN/DNS/Both) | General (feature modules) |
| Appearance | Appearance |
| Startup | Startup |
| WiFi settings | Automation |
| WireGuard behaviour | WireGuard |
| DNS behaviour | DNS |
| Notifications & history | Notifications & history |
| Done | About (version/update) |

Keep this 1:1 mapping as a rule: when a new *common* setting is added to a tab, consider adding it to that
tab's wizard step; when it's advanced, leave it in Settings only. This guarantees the two never drift and
users learn one model.

## Migration approach (incremental, low-risk)

1. **Rename/rehome, don't rewrite.** Move existing cards between `Page*` panels; keep control names and
   handlers so the code-behind and `CommitDraft` keep working. Update `ShowTab` mapping + the sidebar.
2. Introduce the **Automation** tab by moving the WiFi-rules/default-action/trusted cards out of the Wifi
   tab (which becomes "WiFi networks" or merges into Automation).
3. Dissolve **Advanced**: log settings → Notifications & history (already moved), import/export → General,
   theme repo/DLL status → Diagnostics.
4. Add the **Basic/Advanced expander** pattern to the two busiest tabs first (WireGuard, DNS).
5. Add **settings search** last, once cards carry keyword tags.
6. Do it behind the existing `_draft`/`CommitDraft` staging so Save/Cancel semantics are unchanged.

## Open decisions for the owner

- "Automation" vs keeping WiFi rules under a "WiFi" tab - naming preference.
- Whether feature-off tabs should **hide** entirely or show a greyed "enable this feature" stub (stub is
  more discoverable; hiding is cleaner).
- Whether to localize the Basic/Advanced labels now or after the structure settles.
