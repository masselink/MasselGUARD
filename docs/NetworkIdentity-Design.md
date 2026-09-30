# Network Identity (wired + Wi-Fi rules) - Design

**Status:** design (no feature code yet). Branch: `dev`. Target: the version in progress after 4.5.0; number and codename TBD. This document is authoritative; keep it in sync as code lands (mark steps with a check like `SplitTunneling-Design.md`).

**Headline:** rules stop being "Wi-Fi only". Every connected network, wired or wireless, is described by one **network identity** (SSID, DNS suffix, gateway MAC, subnet). One generic **Network** rule kind matches on any of them, feeds the same precedence as today, and drives both the tunnel action and the DNS action. There is no separate "LAN" rule type.

Decisions taken (from design discussion):

1. **Primary network follows Windows** (best default route) by default, with an override: Follow Windows / Prefer wired / Prefer Wi-Fi.
2. **Tunnel = one decision from the primary network. DNS = evaluated per adapter.**
3. **The Trusted-networks list accepts identities**, not only SSIDs, and every entry field has a **fetch** button that reads the current network.

---

## 1. Where this fits our stack

Today the whole pipeline is Wi-Fi shaped:

```
WiFiService.SsidChanged(ssid, isOpen)
  -> MainViewModel.ApplyWifiState(ssid, isOpen)
       -> RuleEngine.EvaluateWifi      (tunnel action)
       -> RuleEngine.EvaluateDns       (DNS action, DnsPolicy.Evaluate(cfg, ssid, isOpen, now))
```

`DnsService` writes DNS to `WiFiService.CurrentInterfaceGuid`. A cable produces no WLAN event, so on a wired network the engine sees "no network" and falls through to the Default / open-network rules, which is wrong.

The change is to replace the `(ssid, isOpen)` pair with a **`NetworkSnapshot`** and keep everything downstream (precedence, `ApplyRuleResult`, resolver ownership, DNS lifecycle) unchanged in spirit.

---

## 2. Concepts

### 2.1 Network identity

Everything we can learn about one connected adapter, none of it requiring an outbound request:

| Field | Source | Notes |
|---|---|---|
| `AdapterId` | `NetworkInterface.Id` (GUID) | key for per-adapter DNS writes |
| `AdapterName` | `NetworkInterface.Name` | display only ("Ethernet 2", "Wi-Fi") |
| `Kind` | `NetworkInterfaceType` | `wifi` (Wireless80211) or `wired` (Ethernet/GigabitEthernet); other types ignored |
| `Ssid`, `IsOpen` | WLAN API (existing) | wifi only; `IsOpen` is always false for wired |
| `DnsSuffix` | `IPInterfaceProperties.DnsSuffix` | connection-specific suffix from DHCP, e.g. `corp.example.com`; lower-cased |
| `GatewayMac` | default gateway IP -> ARP (`GetIpNetTable2` / `SendARP`) | normalised `aa:bb:cc:dd:ee:ff`; null until the gateway has been resolved |
| `Gateway` | `GetIPProperties().GatewayAddresses` | IP, informational and used for the ARP lookup |
| `Subnets` | `UnicastAddresses` (IP + prefix length) | list of CIDRs, e.g. `10.20.4.0/24` |
| `DhcpServer` | `DhcpServerAddresses` | informational (shown in fetch picker) |
| `RouteMetric` | IPv4 interface metric + default-route metric | used only to pick the primary network |

`DnsSuffix`, `GatewayMac`, `Subnets` are available for **Wi-Fi too**, so a rule can say "any network with suffix corp.example.com" whether it arrives over cable or radio.

Not used in v1: the Windows network profile name / category (NLM COM API). It is often just "Network 2" or "Unidentified network" and adds a COM dependency. It can be added later as a fifth `MatchBy` without changing the model.

### 2.2 Connected adapters and the primary network

`NetworkSnapshot` = the list of identities for adapters that are **up, physical (Wi-Fi or Ethernet) and have a usable address** (reuse the existing `IsRelevantAdapter` / `IsUsableAddr` logic from `SystemDiagnosticsWindow`: not loopback, not APIPA / link-local). It excludes:

- the WireGuard tunnel adapters (`NetworkInterfaceType.Tunnel`, and any adapter owned by an active MasselGUARD tunnel), otherwise our own connect would look like a network change;
- virtual switches, VPN pseudo-adapters, Bluetooth PAN.

One entry is flagged **Primary**: the adapter that carries the default route.

**Primary selection** (`AppConfig.PrimaryNetworkMode`, string enum, default `"windows"`):

| Mode | Behaviour |
|---|---|
| `windows` (default) | lowest effective default-route metric wins (interface metric + route metric, IPv4 first, IPv6 tiebreak). This is what actually carries traffic, and wired normally beats Wi-Fi because Windows gives it a lower interface metric. |
| `wired` | a connected wired adapter always wins, Wi-Fi only when no wired adapter is up |
| `wifi` | the reverse |

If nothing is connected the snapshot is empty (same as "no SSID" today).

### 2.3 Tunnel vs DNS scope

- The **tunnel action is global** (a tunnel is up or it is not), so it is decided from the **Primary** network only.
- The **DNS action is per interface** (`DnsService` already writes per adapter), so it is evaluated **once per connected adapter**, each with its own identity. If Windows flips the primary from Wi-Fi to Ethernet, the Ethernet adapter already has its DNS applied, so there is no gap or leak.
- Resolver ownership is unchanged: while any tunnel is active, its own DNS supersedes and DNS actions are held, then re-asserted on the tunnel's falling edge.

---

## 3. Data model

### 3.1 `Models/TunnelRule.cs`

The rule kind `wifi` becomes **`network`**. Old files keep working: `Kind == "wifi"` is read as an alias.

```csharp
// existing: Kind ("wifi" | "schedule" | "trusted") -> ("network" | "schedule" | "trusted"; "wifi" accepted)
public string MatchBy    { get; set; } = "ssid";   // "ssid" | "dnssuffix" | "gatewaymac" | "subnet"
public string MatchValue { get; set; } = "";       // for MatchBy=="ssid" this mirrors Ssid
// existing Ssid stays for compat; MatchValue is authoritative when MatchBy != "ssid"
```

Migration on load: a `wifi`/`network` rule with only `Ssid` set gets `MatchBy="ssid"`, `MatchValue=Ssid`. Nothing to migrate for `schedule`. Managed-preset export/import (`PresetService` "Rules") carries the new fields for free.

`TunnelRule.SsidDisplay` becomes `MatchDisplay` (label + value, e.g. `Gateway MAC aa:bb:...`).

### 3.2 Trusted-networks list

`AppConfig.TrustedNetworks` stays a `List<string>` so existing configs and presets load unchanged. A bare string is an **SSID** (today's behaviour). New entries use a prefix:

| Entry | Meaning |
|---|---|
| `Office-WiFi` | SSID (unchanged) |
| `suffix:corp.example.com` | DNS suffix |
| `mac:aa:bb:cc:dd:ee:ff` | gateway MAC |
| `subnet:10.20.0.0/16` | subnet (containment: any adapter subnet inside it) |

`NetworkMatcher.IsTrusted(cfg, identity)` (pure, shared) is the single place that interprets the list. It replaces the current `TrustedNetworks.Contains(ssid)`.

### 3.3 `Models/NetworkIdentity.cs` (WPF-free, CLI-shared via the Models glob)

```csharp
public record NetworkIdentity(
    string AdapterId, string AdapterName, string Kind,        // "wifi" | "wired"
    string? Ssid, bool IsOpen,
    string? DnsSuffix, string? GatewayMac, string? Gateway,
    IReadOnlyList<string> Subnets, string? DhcpServer,
    int RouteMetric, bool IsPrimary);

public record NetworkSnapshot(IReadOnlyList<NetworkIdentity> Adapters)
{
    public NetworkIdentity? Primary => Adapters.FirstOrDefault(a => a.IsPrimary);
}
```

### 3.4 Config additions

| Field | Default | Purpose |
|---|---|---|
| `AppConfig.PrimaryNetworkMode` | `"windows"` | primary selection (2.2) |
| `AppConfig.NetworkSettleMs` | 2000 | debounce window (section 6) |
| `AppConfig.NetworkMatchPriority` | `["gatewaymac","ssid","dnssuffix","subnet"]` | user-orderable priority of the match types (4.3); a permutation of the four `MatchBy` values, repaired on load if a value is missing, duplicated or unknown |

---

## 4. Evaluation

### 4.1 Matching a rule against one identity

`NetworkMatcher.Matches(rule, identity)` (pure):

| MatchBy | Match |
|---|---|
| `ssid` | identity.Ssid equals value (case-insensitive) |
| `dnssuffix` | identity.DnsSuffix equals value, or ends with `.` + value (so `corp.example.com` also matches `eu.corp.example.com`) |
| `gatewaymac` | identity.GatewayMac equals value (normalised); null never matches |
| `subnet` | any identity subnet lies inside the rule CIDR (`CidrMath`, already shared) |

### 4.2 Precedence (unchanged order, generalised)

```
1. off (rules disabled / manual mode)              -> None
2. open network   (Wi-Fi, IsOpen)                  -> the "open network" rule
3. specific network rules                          -> first match wins  (4.3)
4. trusted-network rules (directional)             -> first match on its side wins
5. schedule rules                                  -> as today
6. default
```

Steps 4 to 6 are exactly today's logic. Only "SSID matched" (3) and "is trusted" (4) call the matcher instead of comparing strings.

### 4.3 Ordering inside the "specific network" step

Candidate rules are ranked **by match type first, list order second**. The match-type order is a **user setting**, `AppConfig.NetworkMatchPriority`, so the user decides, for example, whether a gateway MAC beats an SSID or the other way round.

Default order (highest priority first):

| Priority | MatchBy |
|---|---|
| 1 | `gatewaymac` |
| 2 | `ssid` |
| 3 | `dnssuffix` |
| 4 | `subnet` (longer prefix before shorter, inside this type) |

Rules of the same type keep the order of the rules list, as today. First enabled match wins.

**Setting (Settings, Automation tab, "Network match priority"):** a four-row list, one row per match type, reorderable with up/down buttons or drag with drop lines (same interaction as the rule list) and a **Reset to default** button. It shows one line of help: "When several rules match the current network, the type higher in this list wins." The setting is a soft-lockable field in a managed preset (`PresetService` block "Rules"). Loading repairs a damaged value (missing, duplicated or unknown entries are dropped or appended in default order), so a hand-edited `config.json` cannot disable a match type.

Every decision is logged with its reason, including the priority that decided it (e.g. `Matched rule "Home" (gateway MAC, priority 1)`), so a surprising result can be traced without guessing. When two matching rules were separated only by the priority, the log line also names the loser (e.g. `Rule "Guest" (SSID, priority 2) also matched`).

**Why the default puts gateway MAC above SSID** (and when to flip it)

- An **SSID is a label, not an identity.** It is chosen by whoever runs the access point and is reused everywhere: "Guest", "Linksys", "Free WiFi", or the same corporate name at every site. Two unrelated networks can share one SSID, and one network can broadcast several SSIDs (guest and staff on the same router).
- A **gateway MAC identifies the actual router/L3 gateway** you are behind. It stays the same across SSIDs and across wired and Wi-Fi on that network, and it differs between two sites that happen to use the same SSID or the same `192.168.1.0/24` subnet. It is the closest thing to "this exact network".
- So when both a MAC rule and an SSID rule match the same network, the MAC rule is the more deliberate statement and wins. Example: rule A "SSID `Guest` -> Full-VPN" and rule B "gateway MAC of the office router -> disconnect". Connected to the office `Guest` network, both match; B wins because the user pinned that exact router.
- Trade-offs: a MAC rule only matches once the gateway's MAC has been resolved (ARP, section 6), and if the router is replaced its MAC changes and the rule must be re-fetched. An SSID rule survives a router swap but can match a look-alike network. Both are convenience matching, not a security boundary (section 9).
- **When to flip it:** if you manage many access points behind changing routers and rely on names ("always full VPN on `Guest`, whatever router it is"), move SSID above gateway MAC. Then the "Guest" example above reverses: rule A wins on the office `Guest` network.
- Guidance for users (also the hint shown under *Match by*): use **gateway MAC** for a network you own or control (home, office), **SSID** for "any network with this name" (a cafe chain), **DNS suffix** for a managed/corporate network that hands out a suffix over both cable and Wi-Fi, and **subnet** only as a last resort.

### 4.4 Entry points

```csharp
// tunnel: primary only
RuleEngine.EvaluateNetwork(cfg, snapshot.Primary, now)  -> RuleResult   (was EvaluateWifi)

// DNS: one result per adapter
foreach (var a in snapshot.Adapters)
    DnsPolicy.Evaluate(cfg, a, now)  -> DnsResult(Action, ProfileId, Reason) + a.AdapterId
```

`DnsPolicy` stays pure and CLI-shared; its signature changes from `(cfg, ssid, isOpen, now)` to `(cfg, NetworkIdentity?, now)`. `EvaluateWifi`/`EvaluateSchedules` keep skipping DNS-only rules (`DnsPolicy.IsDnsOnly`).

When two adapters produce different **tunnel** answers (only possible if we evaluated both), we do not: the primary decides. The engine logs the ignored one so it is debuggable, e.g. `Rule "Office-WiFi" matched on Wi-Fi but Ethernet is the primary network; using Ethernet`.

---

## 5. Fetch buttons (UI)

Goal: the user never types a MAC or CIDR.

**Rule dialog** (`RuleDialog`, kind Network):

```
Match by  [ Wi-Fi name (SSID) v ]      <- ssid / DNS suffix / Gateway MAC / Subnet
Value     [ corp.example.com      ]  [ Fetch v ]
```

- **Fetch** reads the live `NetworkSnapshot`. One adapter -> fills the value directly. Several adapters -> a small drop-down: `Ethernet 2 (Primary)  corp.example.com`, `Wi-Fi  Home-5G`, and the field fills with the chosen row's value for the current *Match by*.
- If the chosen field is empty for that adapter (no DNS suffix, gateway not yet resolved) the button says so ("No DNS suffix on Ethernet 2") instead of filling nothing.
- The existing **recent networks** picker (SSID history) stays for `ssid`, and gains recent wired identities once history records them (phase 3).

**Trusted networks editor** (Settings, Automation): same row layout, one entry per line, each with a **Fetch** button and a `Match by` selector; the prefix syntax of 3.2 is written for the user.

**CLI**: `masselguardcli network status` (read-only, non-elevated like `dns status`) prints the snapshot, primary, and the tunnel/DNS result the current rules would give. `--json` supported.

**Diagnostics**: the System diagnostics window's adapter card gets the new fields (suffix and gateway MAC are already partly there) and a "Primary" marker.

---

## 6. Detecting changes

Sources:

- WLAN notifications (existing `WiFiService`) for Wi-Fi.
- `NetworkChange.NetworkAddressChanged` and `NetworkAvailabilityChanged` for cable plug/unplug, DHCP renewals and address changes. These are managed and need no COM.

Rules for handling them:

1. **Debounce** (`NetworkSettleMs`, default 2 s): a dock plugging in produces a burst of events; take one snapshot when the burst ends.
2. **Ignore our own tunnels**: events caused by a MasselGUARD adapter coming up are filtered by the exclusion in 2.2, and a snapshot that is identical to the previous one (same adapters, same identities, same primary) triggers **no** action.
3. **Gateway MAC lags**: right after a link comes up the ARP entry may not exist. If a rule of `gatewaymac` type exists and the MAC is null, retry after 1 s and 3 s before evaluating with "no match".
4. The existing `ApplyWifiState` becomes `ApplyNetworkState(snapshot)`; the WLAN path just triggers a fresh snapshot. Intentional-disconnect marking, auto-reconnect, and the ownership handoff are unchanged.

---

## 7. DNS lifecycle changes

`DnsService` already snapshots the original DNS to `dns_state.json` before the first override and restores on exit and after a crash. Extension:

- Apply / revert take an **adapter GUID** (from the identity) instead of `WiFiService.CurrentInterfaceGuid`.
- `dns_state.json` becomes keyed by adapter GUID (a list of per-adapter originals). Old single-entry files are read as one entry on first load.
- When an adapter disappears (cable unplugged) its saved original is dropped without writing anything to a device that is gone. On exit only adapters that still exist are restored.
- Resolver ownership (section 2.3) is per adapter but triggered by "any tunnel active", as today.

---

## 8. Worked examples

Assume defaults (`PrimaryNetworkMode = windows`, Default rule = "disconnect") and these rules:

| # | Kind | Match | Tunnel | DNS |
|---|---|---|---|---|
| R1 | Network | DNS suffix `corp.example.com` | disconnect | Corp DNS |
| R2 | Network | Gateway MAC `aa:bb:cc:00:11:22` (home router) | disconnect | Quad9 |
| R3 | Trusted (untrusted side) | list: `Home-5G`, `suffix:corp.example.com`, `mac:aa:bb:cc:00:11:22` | `Full-VPN` | - |
| R4 | Network | Subnet `10.0.0.0/8` | `Split-Corp` | - |

**Example 1: office desk, docked, Wi-Fi also on.**
Snapshot: Ethernet (suffix `corp.example.com`, subnet `10.20.4.0/24`, metric 25, **primary**) + Wi-Fi `Guest` (metric 50).
- Tunnel: primary is Ethernet. R1 (priority 3, DNS suffix) matches before R4 (priority 4, subnet) -> disconnect (already on the trusted corporate network). R3 is not consulted because a specific rule matched.
- DNS: Ethernet -> R1 -> Corp DNS. Wi-Fi `Guest` -> nothing matches -> Automatic (unchanged).
- Log: `Primary network: Ethernet 2 (corp.example.com). Wi-Fi "Guest" is secondary; DNS evaluated separately`.

**Example 2: undock and walk to a cafe.**
Ethernet goes down, snapshot changes to Wi-Fi `Cafe-Free` (open network).
- Tunnel: step 2 (open network) -> the open-network rule activates `Full-VPN`.
- DNS: the Ethernet adapter's override is dropped (adapter gone); Wi-Fi gets the open-network DNS profile, or is held while the tunnel owns the resolver.

**Example 3: home, wired laptop, Wi-Fi off.**
Ethernet, no DNS suffix, gateway MAC `aa:bb:cc:00:11:22`.
- Tunnel: R2 matches (priority 1, gateway MAC) -> disconnect. DNS -> Quad9.
- Right after plug-in the MAC may be unresolved: the engine retries the ARP lookup at 1 s and 3 s, then evaluates. In between it changes nothing (no flapping).

**Example 4: hotel Ethernet, subnet collides with home.**
Hotel network is `192.168.1.0/24` with an unknown gateway MAC and no suffix. A rule of Subnet `192.168.1.0/24` would wrongly trust it, which is why the picker for Match by hints **"Subnets are shared by many networks; prefer Gateway MAC or DNS suffix"**. R2 (MAC) does not match, R3 treats it as untrusted -> `Full-VPN` activates.

**Example 5: Windows and the override disagree.**
`PrimaryNetworkMode = wifi`, both connected. The Wi-Fi network decides the tunnel even though Windows routes over Ethernet. DNS is still per adapter. Log: `Primary network forced to Wi-Fi by setting (Windows would use Ethernet)`.

**Example 6: trusted list with identities.**
Default is set to "protect on untrusted". On a wired hotel network with no listed identity -> untrusted -> `Full-VPN`. Add the hotel's gateway MAC via **Fetch** in the Trusted editor -> the next snapshot is trusted -> disconnect.

---

## 9. Compatibility, localization, sharing

- **Backward compatible:** old configs/presets load; `wifi` kind and bare SSID entries keep meaning what they did. New keys are additive, and an older build ignores them.
- **Shared code:** `NetworkIdentity.cs`, `NetworkMatcher.cs`, `NetworkMonitor.cs` (snapshot builder, uses `System.Net.NetworkInformation` only, no WPF) and the changed `RuleEngine`/`DnsPolicy` are WPF-free. **New `Services/*.cs` must be added to `MasselGUARDcli.csproj`'s explicit `<Compile Include>` list** (`Models` are globbed). `DnsService` stays GUI-only.
- **Localization:** all new UI strings (Match by choices, Fetch, hints, primary-mode setting, log-visible labels excluded per the English-logs rule) go through `Lang.T` with keys in all 12 `lang/*.json`. New rule reasons (`RuleEngine`/`DnsPolicy`) stay English as data and need `MainViewModel.LocalizeReason` mappings.
- **Not a security boundary:** MAC and DNS suffix can be spoofed by whoever controls the network. Trust decisions based on them are convenience, not enforcement (same soft-lock caveat as the managed preset). Documented in Settings help text.
- **Privacy:** the snapshot is local-only (adapter properties + ARP table). No outbound probe is used to identify a network.

---

## 10. Tests (`MasselGUARDcli selftest`)

Pure logic, so it joins the existing self-test:

- `NetworkMatcher`: each `MatchBy`, suffix subdomain rule, MAC normalisation (`-`/`:`/case), null gateway MAC never matches, subnet containment.
- Priority ordering: with the default `NetworkMatchPriority`, gateway MAC before SSID before suffix before subnet, longer prefix first, list order as tiebreak; a MAC rule beats an SSID rule that also matches (the "Guest" example in 4.3).
- Custom priority: with SSID moved above gateway MAC the result flips; a reordered list is honoured for every pair of types.
- Priority repair: missing, duplicated or unknown entries in a damaged `NetworkMatchPriority` are repaired to a valid permutation.
- Primary selection: metric ordering, `wired` and `wifi` overrides, single adapter, empty snapshot.
- Trusted list: bare SSID, `suffix:`, `mac:`, `subnet:` entries; mixed lists.
- Tunnel-from-primary vs DNS-per-adapter, with the dual-adapter example (Example 1).
- Migration: `wifi` kind + `Ssid` becomes `MatchBy=ssid`.
- Debounce/idempotence: identical snapshot triggers no action (unit-level on the diff function).

---

## 11. Implementation plan

1. [x] (code written; verify with `dotnet build` + `MasselGUARDcli selftest`) `NetworkIdentity`/`NetworkSnapshot` models + `NetworkMonitor` (snapshot, primary, gateway MAC via ARP) + `NetworkMatcher`; add to CLI csproj; selftest cases.
2. [x] `TunnelRule` fields + migration; `RuleEngine.EvaluateNetwork` and `DnsPolicy.Evaluate(cfg, identity, now)`; trusted-list prefix parsing. (Done: `TunnelRule.MatchBy`/`MatchValue` with `IsNetworkKind`/`EffectiveMatchBy`/`EffectiveMatchValue` (no load-time migration needed: an empty `MatchBy` means SSID and the SSID rule value stays in `Ssid`); `NetworkMatcher.MatchingRules`; `EvaluateWifi`/`Evaluate(cfg, ssid, ...)` kept as thin bridges via `NetworkMatcher.FromSsid`; `RuleResult.Details` carries the priority/loser log lines for phase 3. The persisted kind stays `"wifi"` until the phase 5 dialog writes `"network"`; both are accepted everywhere. `RuleEngine.cs` is now CLI-shared and self-tested.)
3. [x] `MainViewModel.ApplyNetworkState`, `NetworkChange` wiring + debounce, primary log lines; tunnel from primary, DNS per adapter. (Done as `MainViewModel.ApplyNetworkSnapshot` + `Services/NetworkWatcher`. Notes: the tunnel action re-runs only when the PRIMARY identity changed and DNS only for adapters whose identity changed, so a snapshot that differs only in a secondary adapter never re-fires the tunnel rule or bumps its counter; an empty snapshot while Wi-Fi is associated but has no address yet is treated as "DHCP pending", not a disconnect; the gateway ARP lookup only runs when a MAC rule or `mac:` trusted entry exists; IPv6 /128 host addresses are excluded from `Subnets` because SLAAC privacy addresses rotate. When an adapter leaves, its DNS override is restored rather than dropped, because a static DNS written by netsh persists on the adapter while it is unplugged.)
4. [ ] `DnsService` per-adapter apply/revert + `dns_state.json` keyed by adapter.
5. [x] (done except the wizard text and drag-reorder: the match-priority list has up/down buttons and Reset; the rule dialog writes kind `"network"`; Fetch in the rule dialog and the trusted-networks editor; primary-mode + priority in Settings, preset-lockable; `network status` CLI; diagnostics sections) UI: `RuleDialog` (Match by + Fetch), trusted-networks editor with Fetch, primary-mode setting, "Network match priority" list (up/down + drag, reset, preset-lockable), wizard step text, diagnostics fields, CLI `network status`. All 12 languages.
6. [x] (done: the history follows the PRIMARY network only, so the band never overlaps; `WifiHistoryEntry` gained `Kind`/`AdapterName`/`DnsSuffix`/`GatewayMac`, with `Ssid` staying the display label (SSID, else DNS suffix, else adapter name); recording moved from the WLAN handler to `MainViewModel.PrimaryNetworkChanged`; tooltip shows a plug icon + "Wired"; CLI `wifi-history` / `network-history` show a Type column. Not done: a recent-networks picker, because the rule dialog has none: Fetch reads live networks instead.) History: record wired identities beside the WiFi history (extend `WifiHistoryEntry` with optional `Kind`/`AdapterName`/identity) so the timeline band and the recent-networks picker include wired networks.

Phases 1 to 3 give working wired rules; 4 makes DNS correct with two adapters; 5 to 6 are the UX and history polish.

---

## 12. Open questions

- **Gateway MAC on IPv6-only networks:** ARP does not apply; neighbour discovery gives the MAC via `GetIpNetTable2` as well. v1 covers IPv4 gateways and uses NDP where the same call returns it.
- **Multiple gateways / VLAN trunks:** the first default gateway with a resolvable MAC is used.
- **NLM profile name / domain category** as a fifth `MatchBy`: deferred until there is demand.
- **Should History show the secondary network as its own band?** Decided in phase 6.
