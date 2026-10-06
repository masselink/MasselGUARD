# The CLI self-test

`MasselGUARDcli.exe selftest` runs MasselGUARD's built-in checks of its pure logic: the code that decides things (which rule fires, which DNS profile applies, which routes a split tunnel gets) without touching the network, the driver, the firewall or any file. It is the quickest way to find out whether a change broke the rule engine, and it is what a release is checked with.

```
MasselGUARDcli selftest
```

```
✓ Self-test: 457 passed (CidrMath 18, Backend 11, Export 6, DnsPolicy 20, NetworkMatcher 150, RuleTester 38, RuleEngine 37, RuleSimulator 20, TempOverride 18, CommandPipe 17, Shortcut 31, PrivilegedOps 10, PrivilegedRpc 68, DnsHoldKeeper 13).
```

## Running it

- **No administrator rights needed.** `selftest` is one of the read-only commands that skip the elevation gate (with `help`, `version`, `dns` and `network`), so it runs in any terminal.
- **Hidden command.** It is not listed in `help`, because it is a developer and support tool.
- **Exit code:** `0` when every check passed, `1` when at least one failed. A failed check is printed as `FAIL <suite> <description>`, one line each, followed by `Self-test: N passed, M failed.`
- **Quick and safe.** It is fast, reads no configuration, writes nothing and never connects a tunnel. Nothing it checks depends on the PC it runs on, so the same build gives the same result everywhere.
- **From the app.** *Settings > Diagnostics > Tester* runs the CLI `selftest` as one of its steps and reports pass or fail.

## What it covers

The checks are plain assertions inside the shared, WPF-free services, so the CLI and the GUI are tested with exactly the same code. They are grouped in eleven suites, in the order printed:

| Suite | Source | What it checks |
|---|---|---|
| **CidrMath** | `Services/CidrMath.cs` | The set arithmetic behind split tunneling: the effective `AllowedIPs` for *off*, *include* and *exclude* modes (for example `0.0.0.0/0` minus `10.0.0.0/8`), edge cases such as IPv6 being kept when only IPv4 is excluded, and, for large results, a property check that no resulting block overlaps an exclude and that nothing was dropped except what the excludes cover. |
| **Backend** | `Services/SplitTunnelBackend.cs` | The `.conf` rewrite for split tunneling: *off* and an empty range list leave the config untouched, *include* turns `AllowedIPs` into only the included ranges and keeps every other line, *exclude* removes the excluded block while keeping IPv6, and kill-switch bypass ranges exist only in exclude mode. |
| **Export** | `Services/TunnelExportService.cs` | The tunnel export and import round trip: split settings and the per-tunnel `# MasselGUARD-` comment lines survive export and import, and an *off* or empty split is not written. |
| **DnsPolicy** | `Services/DnsPolicy.cs` | Which DNS profile applies, in the real precedence order, at a fixed time inside a Monday to Friday 09:00 to 17:00 window: master switch and manual mode, the DNS feature being off, open-network profile, network rules, disabled rules being ignored, trusted and untrusted sides, rule beats trusted list, schedule windows, default profile, *automatic*, *none*, a missing profile id, and the DNS-only rule helper. |
| **NetworkMatcher** | `Services/NetworkMatcher.cs` | The matching of a network against a rule, the largest suite:<br>• MAC address normalisation (every spelling becomes one form, junk becomes nothing).<br>• Each match type: SSID, DNS suffix, gateway MAC, subnet (including several subnets in one rule), connection type, and the device itself (adapter name, description, own MAC) with comma-separated "any of" lists.<br>• Trusted-list entries (`suffix:`, `mac:`, `subnet:`).<br>• Conditions: AND, "is not", a negated gateway MAC while the MAC is unresolved, the legacy single match, and a JSON round trip of the conditions.<br>• Rule order: first match in the table wins, whatever the match type.<br>• Primary network selection (Windows, wired first, Wi-Fi first).<br>• History labels and keys, and the snapshot fingerprint.<br>• The conditions suggested for "Create a rule from this network" (SSID, DNS suffix, gateway MAC, subnet; nothing in Simple Wi-Fi mode for a cable), and that a suggestion matches the network it came from and no other. |
| **RuleTester** | `Services/RuleTester.cs` | The text behind the **Test rule** button: "requirement met / not met" for SSID rules, other network rules, multi-condition rules, trusted rules, schedule rules, and the notes (disabled rule, manual mode, Simple Wi-Fi mode). |
| **RuleEngine** | `Services/RuleEngine.cs` | The decision for a whole configuration: the legacy SSID entry point still behaves as before; an office example where the table decides and only the winner's counter moves; Simple Wi-Fi mode (advanced rules never fire, trusted and schedule rules are not restricted); gateway MAC versus SSID in either table order; trusted list with identities; open network, no network, disabled rule, manual mode and tunnels off; and DNS evaluated per adapter (a docked office with a guest Wi-Fi also connected). |
| **RuleSimulator** | `Services/RuleSimulator.cs` | The reasoning behind **Advanced test**: the on/off gates, trusted rules and the default action, Simple Wi-Fi mode ignoring a wired network, and that the described network is normalised. The simulator runs the real engine on a copy of the configuration, so a simulation does not move the real hit counters. |
| **TempOverride** | `Models/TempOverride.cs` | The timer behind the timed DNS override ("use this profile for 1 minute"): a fresh override is active until its absolute UTC end time and expired afterwards, the remaining time and the `0:42` countdown text (rounded up, hours for long ones), replacing an override restarts the clock, the 10 s / 1 / 5 / 15 minute presets and their menu label keys. |
| **CommandPipe** | `Services/CommandPipe.cs` | The command channel behind `dns bypass` and the Windows right-click menu: parsing of `bypass`, `bypass 60`, `bypass stop`, `bypass toggle` (case, extra spaces, the 5 to 3600 second clamp, garbage and other commands rejected), and a real round trip over a private named pipe: the access-protected server starts, the same user connects, the request arrives and the reply comes back, and a missing listener returns nothing. It uses a random pipe name, so a running MasselGUARD window is never touched. |
| **Shortcut** | `Models/Shortcut.cs` | The text form of keyboard shortcuts stored in `config.json` (the bypass shortcut): any case and the aliases (`Control`, `Windows`), spaces and modifier order normalised to `Ctrl+Alt+Shift+Win+Key`, letters, digits, F1 to F24 and the named navigation keys, empty text meaning "no shortcut", and rejection of Shift-only or modifier-less shortcuts, two keys, stray plus signs, unknown keys and F25. Also the fallback to the default for an unusable value. |

## What it does not cover

The self-test is a safety net for logic, not a test of the whole program. It does **not** exercise:

- the GUI (layout, themes, drag and drop, the footer, the settings pages)
- connecting or disconnecting a tunnel, the kill switch (Windows Firewall), `netsh` DNS changes or the WireGuard driver
- the live network (it works from described networks, never from your adapters), Wi-Fi notifications or the gateway MAC lookup
- configuration loading, managed presets, the update check or history files
- translations (the language files are not compared by the self-test)

For those, build and use the app: `BUILD.bat x64 nozip run`, then the **System diagnostics** window and `MasselGUARDcli network status` show the live picture.

## When to run it

- After any change to rule, DNS, network matching or split-tunnel code, and before a release. It must pass on a clean build.
- When a rule does not fire as expected: if the self-test passes, the logic is fine and the cause is the data (the network the PC sees, the rule's conditions, the table order). Use **Test rule** or **Advanced test** next.

## Adding a check

Each suite is one `RunSelfTest()` (or `SelfTest()`) method returning `(passed, failed, failures)` in the service file named above. Add the assertion there with a short description, and keep the code WPF-free, since the CLI shares it. A new suite needs a line in `CmdSelfTest` in `Cli/CliRunner.cs` (the call, the `FAIL` printing, and the two totals), and a new shared `Services/*.cs` file must be added to `MasselGUARDcli.csproj` so the CLI compiles it. Keep the activity-log style rule in mind: the failure text is English data.
