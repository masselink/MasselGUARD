# Automation shortcuts - design

Status: **sections 1 (create a rule from this network) and 3 (timed DNS override + bypass profile) are implemented in 4.6.0 (phase 1: footer menu and the Add drop-down; the toast button and tray item are still open); section 2 (pause automation) is planned, not started.** Three small features that make the existing automation and DNS control easier to live with. None of them changes how rules are evaluated.

1. **Create a rule from this network** - one click from the network you are on to a pre-filled rule dialog.
2. **Pause automation for N minutes** - a timed pause that ends by itself, instead of the manual-mode switch people forget to turn back on.
3. **Use a DNS profile temporarily** - for example Google DNS for 1 minute to get past the ad-block, then back to automatic.

All three reuse what exists: the rule dialog and its Fetch machinery, `NetworkMatcher`, the primary network (`MainViewModel.CurrentNetwork`), the interactive toast, the tray menu and the 1 second poll.

---

## 1. Create a rule from this network

### Goal
Today a rule is built by opening **+ Add** and picking conditions, then using Fetch to fill values. For the common case ("I am on the network I want a rule for") that is more steps than needed. The shortcut opens the same dialog with the network's identity already entered.

### Entry points
| Where | What the user sees | Phase |
|---|---|---|
| **Footer** network label (`WifiFooterLabel`) | Right-click menu: **Create rule from this network…** (or **Edit rule "X"…** when a rule already matches, see below). Other connected networks are listed as extra items. | 1 |
| **Automation panel** | The **+ Add** button gets a small drop-down: **Add**, **Add from current network…** | 1 |
| **Network-change toast** (Settings > Notifications, mode *Only when no rule matched*) | An action button **Create rule** on the interactive toast. This is the natural moment: the toast says "no rule matched this network". | 2 |
| Tray menu | Same item as the footer. | 2 |

### What is pre-filled (pure helper, selftested)
A new pure function `NetworkMatcher.SuggestConditions(NetworkIdentity id, bool simpleMode)` returns the recommended conditions:

| Network | Suggested condition(s) |
|---|---|
| Wi-Fi with an SSID | `SSID is <ssid>` (one condition) |
| Wired with a DNS suffix | `DNS suffix is <suffix>` |
| Wired without a suffix, gateway MAC known | `Gateway MAC is <mac>` |
| Wired with neither | `Subnet is <first IPv4 subnet>` |
| Anything, **Simple Wi-Fi mode** on | Wi-Fi: SSID as above. Wired: nothing; the menu item is disabled with the tooltip "Wired networks are not used in Simple Wi-Fi mode". |

Rules of thumb: one condition by default (the least brittle identifier), never an adapter MAC or a rotating address. The dialog shows a hint line, "Added: SSID is Office. Use + Add condition to combine it with the gateway MAC or a subnet", and the user adds more with the normal controls.

The gateway MAC needs an ARP lookup, which `NetworkMonitor` only does on demand. The footer action therefore captures with `resolveGatewayMac: true` (the same capture the dialog's Fetch button already uses), never at startup.

### Dialog changes (`Views/RuleDialog`)
- New optional constructor parameter `IReadOnlyList<RuleCondition>? prefillConditions`. It creates the condition rows but keeps **add mode** (no hit counter, name still auto-generated from the conditions). The existing `existingConditions` parameter implies edit mode, so a separate parameter is needed.
- The title bar says "New rule from <network label>".
- No other change: tunnel and DNS pickers, name, OK/Cancel stay as they are.

### Existing rule that already matches
Before opening the dialog, run `NetworkMatcher.MatchingRules(cfg, identity)`:
- **Nothing matches** - open the new-rule dialog as above.
- **A rule matches** - the menu item reads **Edit rule "X"… (position N)** and opens that rule instead. A second item, **Create another rule anyway…**, opens the pre-filled dialog. This avoids accidental duplicates and shows the first-match-wins position.
- After saving a new rule, if an *earlier* rule also matches this network, show a themed note: "Rule X above also matches this network and wins. Drag the new rule above it to change that." (This is the same information the decision log already writes.)

### Edge cases
- No network at all (offline): the items are disabled.
- Several networks connected: the footer shows the primary one; the menu lists each other connected network as **Create rule from <name>…**.
- Open Wi-Fi: the rule dialog is unchanged; the existing open-network protection still runs before rules.
- The rule is appended at the end of the table (lowest priority), as **+ Add** does today.
- Locked automation block (managed preset): hide the items, same gating as Add/Edit (`ApplyPolicyGating`).

### Touch points
`Services/NetworkMatcher.cs` (SuggestConditions + selftest cases), `Views/RuleDialog.xaml(.cs)` (prefill parameter, title, hint), `MainWindow.xaml(.cs)` (footer context menu, **+ Add** drop-down, a `CreateRuleFromNetwork(NetworkIdentity)` method next to `WifiRuleAdd_Click`), `ViewModels/MainViewModel.cs` (toast action, phase 2), `App.xaml.cs` (tray item, phase 2), `lang/*.json` (about 8 keys), `docs/MANUAL.md`, `docs/WHATSNEW.md`, `CLAUDE.md`.

### Selftest cases to add (NetworkMatcher suite)
Wi-Fi gives one SSID condition; wired with suffix gives the suffix; wired without suffix gives gateway MAC, then subnet; Simple Wi-Fi mode on a wired network gives nothing; an empty or null identity gives nothing; the result round-trips through `RuleCondition` and matches the identity it came from (`NetworkMatcher.RuleMatches` is true).

---

## 2. Pause automation for N minutes

### Goal
Sometimes you need automation off for a while (a captive portal login that a tunnel would block, a speed test, a vendor's remote session). The manual-mode switch works but is permanent, and people forget it. A pause ends by itself.

### Behaviour
- **Pause** stops automation from acting: network rules, the open-network rule, trusted-network and schedule rules **and** DNS automation. It does not undo anything already applied: a connected tunnel stays connected and the current DNS stays.
- **Resume** (automatic at the end, or manual) re-evaluates the current network once, so the right tunnel and DNS apply immediately.
- The pause is **runtime only**: it is not written to `config.json`, so restarting the app always resumes automation. (An open question below asks whether it should survive a restart.)
- If **manual mode** is already on there is nothing to pause; the item is disabled.

### Where it appears
| Place | What |
|---|---|
| **Tray menu** | **Pause automation** submenu: 15 minutes, 1 hour, 4 hours. While paused it becomes **Resume automation (paused until 14:30)**. |
| **Footer** | While paused, a `⏸ Automation paused until 14:30` item (click to resume). Hidden otherwise. |
| **Automation panel header** | A small `⏸` chip with the same tooltip, so it is visible without the footer. |
| **Title-bar Automation button** | Tooltip mentions the pause. |
| **Activity log** | `Automation paused for 1 hour (until 14:30)` and `Automation resumed` (Ok level, so Normal log level shows them). |
| **Test rule / Advanced test / diagnostics / `network status`** | Say "automation is paused until 14:30", the same way they already say "manual mode is on". |

A custom duration (minutes box) is a phase 2 item; the three presets cover the common cases.

### State and code
- `AppConfig` gets a **`[JsonIgnore] DateTime? PausedUntilUtc`** and a computed **`bool AutomationPaused(DateTime nowUtc)`** = `ManualMode || (PausedUntilUtc != null && nowUtc < PausedUntilUtc)`. `DeepClone` copies the pause so the simulator reports it.
- Every place that currently reads `cfg.ManualMode` as "automation off" switches to the helper: `RuleEngine` (4), `DnsPolicy` (3), `RuleTester`, `RuleSimulator`, `MainViewModel.CheckSchedules`, `RulesColumnVisibility`, and the UI gating. `ManualMode` stays the persistent setting (Settings, presets, wizard), unchanged.
- `MainViewModel` owns the methods `PauseAutomation(TimeSpan)` / `ResumeAutomation()`, raises a `PauseChanged` event for the footer, tray and panel, and the existing 1 second poll (`_timer`) detects expiry, logs it and calls `EvaluateNetworkNow()` once.
- Absolute UTC time is used, so sleep or a clock change cannot extend or shorten the pause by accident.

### Edge cases
- **Managed preset** that locks the *automation* block (it contains `ManualMode`): hide the pause items, a user must not override a policy for a while either.
- Network changes while paused: history and the footer still follow the network; only decisions are skipped. The network-change toast (if enabled) still works and can say "paused".
- Pausing during the 2 s settle debounce: the pending evaluation checks the pause when it runs.
- The CLI is a separate process and cannot see the pause. `network status` run from the CLI keeps reporting what the rules *would* do and does not mention a pause. (Open question below.)

### Touch points
`Models/AppConfig.cs`, `Services/RuleEngine.cs`, `Services/DnsPolicy.cs`, `Services/RuleTester.cs`, `Services/RuleSimulator.cs`, `ViewModels/MainViewModel.cs` (pause state, poll, event), `App.xaml.cs` (tray submenu), `MainWindow.xaml(.cs)` (footer item, panel chip), `lang/*.json` (about 10 keys), `docs/MANUAL.md`, `docs/WHATSNEW.md`, `CLAUDE.md`.

### Selftest cases to add (RuleEngine / DnsPolicy / RuleTester suites)
Paused engine returns no action for network, open-network, trusted and schedule paths; the same config after expiry acts normally; `ManualMode` still wins; DNS policy returns `None` while paused; `RuleTester` and the simulator mention the pause; a clone keeps the pause.

---

## 3. Use a DNS profile temporarily (timed override)

### Goal
"Use Google DNS for 1 minute to get past the ad-block, then go back." Switching a DNS profile by hand already works (the DNS panel's **Enable** button and the tray's DNS submenu call `MainViewModel.ManualApplyDns`; **Revert to default** calls `ManualRevertToDefault`), but a manual choice **sticks until you revert it**, so you have to remember to switch back. The new part is the **timer**: pick a profile and a duration, and MasselGUARD reverts by itself.

### Behaviour
- **Use for…** applies the profile exactly like a manual **Enable** (it overrides automation and the tunnel's DNS, as manual choices do today) and starts a countdown.
- At the end it calls the same path as **Revert to default**, so automation takes over again and the right DNS for the current network applies. If the user enables or reverts a profile by hand before the end, the timer is cancelled.
- The OS and browsers keep DNS caches, which would defeat a short bypass. Apply and revert both flush the resolver cache (`ipconfig /flushdns`, one more `DnsService` step). Browsers that use their own encrypted DNS ("Secure DNS" in Chrome and Edge, DoH in Firefox) ignore the Windows resolver; the manual should say so.
- Runtime only, like the pause: restarting the app also ends it (the existing exit path already restores the original DNS). Implemented with presets 10 s / 1 / 5 / 15 min, an optional switch (`DnsTempOverrideEnabled`) and a marked **bypass profile** (`BypassDnsProfileId`) for the one-click action; the resolver cache is already flushed by `DnsService.Flush` on every apply and restore.

### Where it appears
| Place | What |
|---|---|
| **Right-click a profile** in the DNS panel | **Use for 1 minute**, **5 minutes**, **15 minutes**, plus the existing Enable. |
| **Tray DNS submenu** | Each profile gets a **For 1 minute / 5 minutes / 15 minutes** sub-list. |
| **DNS panel row and footer** | While active: `Google - 0:42 left` on the row's status and as a footer chip; clicking the chip reverts at once. |
| **Keyboard** | In the window: select the profile row and press **Shift+Enter** (the plain Enter stays "Enable"), which uses the last chosen duration. A **global hotkey** (works from any app) is possible with `RegisterHotKey`, but it needs a settings page to choose the key and handle conflicts, so it is phase 2. |
| **Activity log** | `DNS: using Google for 1 minute (until 14:31)` and `DNS: temporary override ended, back to automatic`. |

### State and code
- `MainViewModel`: `_dnsTempUntilUtc` + `_dnsTempProfileId`; `UseDnsTemporarily(DnsProfile, TimeSpan)` (calls `ManualApplyDns`, sets the end time) and `CancelDnsTemporary()`. The existing 1 second poll detects the end, logs it and calls `ManualRevertToDefault()` + `RebuildDnsPanel()`; `DnsTempRemaining` feeds the row and the footer (read in the 1 second tick, no extra timer).
- `DnsService`: a `FlushResolverCache()` helper, called after every apply and revert (cheap, also fixes the "I switched DNS but the old answer is still cached" surprise for normal manual switches).
- `MainWindow`: a `ContextMenu` on `DnsProfilesPanelList` rows, the footer chip, Shift+Enter; `App.RebuildTrayDnsMenu`: the sub-lists.
- A managed preset that locks the DNS block hides the items (same gating as Enable).

### Edge cases
- DNS feature off: items hidden. No profile selected: the key does nothing.
- A profile that requires encryption (DoH with *Require encryption*) behaves as Enable does today, including failing closed with the existing message.
- Switching to a second profile while one is active replaces it and restarts the timer.
- Sleep or a clock change: absolute UTC end time, so the revert fires on the first tick after wake.

### Touch points and tests
`ViewModels/MainViewModel.cs`, `Services/DnsService.cs` (flush), `MainWindow.xaml(.cs)`, `App.xaml.cs`, `lang/*.json` (about 8 keys), `docs/MANUAL.md`, `docs/WHATSNEW.md`, `CLAUDE.md`. The decision logic is thin, so a selftest is not needed unless the end-time calculation is moved into a pure helper (recommended: a tiny `TempOverride` struct with `IsActive(nowUtc)` / `Remaining(nowUtc)`, selftested for start, end, replace and cancel).

---

## Order of work

1. `NetworkMatcher.SuggestConditions` + selftest.
2. `RuleDialog` prefill parameter, then the footer menu and the **+ Add** drop-down.
3. `AppConfig.AutomationPaused` and the engine switch-over + selftest (the part with regression risk: review every `ManualMode` read).
4. Pause state in `MainViewModel`, tray submenu, footer item, panel chip, log lines.
5. Language keys (12 files), docs, release notes.
6. The timed DNS override (section 3): `UseDnsTemporarily` + the resolver-cache flush, then the DNS-row context menu, the tray sub-lists, the footer chip and the log lines.
7. Phase 2: the rule-shortcut toast button and tray item, a custom pause duration, the global hotkey for the DNS override.

Estimated size: small for each feature. The only part needing care is step 3, since it touches the rule engine; the selftest suites cover it well.

## Open questions
1. Should a pause **survive a restart** (store an absolute end time) or always end at restart? The plan above ends it at restart, the safer default.
2. Should the CLI be able to pause (for example `MasselGUARDcli automation pause 60`)? It would need a persisted end time, which contradicts the runtime-only choice above.
3. Pause lengths: are 15 minutes, 1 hour and 4 hours the right presets?
4. For **Create rule from this network**: is one suggested condition the right default, or should the dialog offer a small checklist of everything the network offers (SSID, DNS suffix, gateway MAC, subnet) to tick?
5. Should the new rule be appended at the end (as **+ Add** does) or inserted above a broader rule that would otherwise shadow it?
