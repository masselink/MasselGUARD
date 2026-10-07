# Handoff - 5.0 (the service back-end)

Status 2026-10-07. Written so a fresh session can pick up without the history. Read `CLAUDE.md` first (the codebase guide); this file says where things stand, what is left before a release, the standing rules and the traps.

## 1. Where things stand

- **4.6.0 - Wired Weasel** is released-ready and documented (`docs/WHATSNEW.md`, `docs/release-body-4.6.0.md`). Check whether it is pushed, tagged and published (`git log origin/main..main`) and whether the Scoop bucket was bumped.
- **5.0 is built on the branch `service-backend`** (the code still says 4.6.0 until release; the target number is **5.0**, written with three parts in the files because the build and `UpdateChecker.ParseVersion` expect `Major.Minor.Patch`, so `5.0.0` there and "5.0" in the text). It contains, in this order of work:
  1. a configurable DNS bypass shortcut (`BypassShortcut`, optional system-wide hotkey),
  2. the **service back-end**: the privileged operations behind `IPrivilegedOps`, a LocalSystem service with a named-pipe RPC, `asInvoker` manifest, one-click install (Settings, wizard, installer, CLI), connect-by-name from a machine-encrypted tunnel store, a timed DNS override the service keeps, the DNS bypass without the window (Explorer menu/CLI), per-user autostart, installing/updating over files in use, **headless automation** (snapshot handover), and release **checksums** for the updater.
  3. **Browse DNS servers**: a searchable, checkbox picker for public DNS resolvers that replaces the fixed "Add presets": add AND remove entries (Apply shows +N new / -M removed), per-entry parameter fields (NextDNS `CONFIG_ID`), a speed test with a changeable test name and an "only ticked servers" option, sorted fastest first. The list lives in its own repo (`masselink/MasselGUARD-dnslist`: `index.json` + `servers/<provider>.json`, language-neutral, read from the `main` branch, cached with an ETag per file, all or nothing, a provider file of an unknown schemaVersion is skipped; NO copy ships with the app, the servers the user adds are saved locally in dns-selected.json). Design, file format and the app pieces: `docs/DnsList-Format.md` and the "DNS server list" bullet in `CLAUDE.md`.
  Everything is described in [`ServiceBackend-Design.md`](ServiceBackend-Design.md); the user-visible text is in `docs/WHATSNEW.md` ("Next version") and `docs/MANUAL.md` chapter 37.
- **Security:** reviewed twice with `/security-review`; findings, fixes and the accepted risks are in [`ServiceBackend-Security.md`](ServiceBackend-Security.md). The service refuses to run from a folder that standard users can write to, so it must be installed from Program Files.
- **Quality gates:** `dotnet build MasselGUARD.csproj` and `MasselGUARDcli\MasselGUARDcli.csproj` build with 0 errors; `MasselGUARDcli selftest` passes **725** checks in 24 suites; all 12 `lang/*.json` have the same key count (1321).
- The user has installed the service on his machine (Program Files, running, used by the app) and is the only one who can test the rest: the elevated app and the service cannot be driven from an assistant session.

## 2. What is left before 5.0 can be released

1. **Run the manual test plan** (`ServiceBackend-Design.md` section 10) on a real machine, including a second user, a sign-out/in for the autostart, headless automation (exit the window, change network), and an update from one release to the next. Nothing beyond "service installs and is used" has been verified outside the selftest.
2. **Merge**: `service-backend` into `dev` (and later `main`). `dev` should be merged into `service-backend` first if it moved; the 12 `lang/*.json` conflict at the end of each file when both sides add keys: keep both blocks.
3. **Version bump** (all together): `UpdateChecker.cs` (`CurrentVersion` + `_codenames`), `BUILD.bat` (`VERSION` + `CODENAME`), both csproj `<Version>`/`AssemblyVersion`/`FileVersion`/`InformationalVersion`. Pick a codename. Rename the "Next version" heading in `docs/WHATSNEW.md`, write `docs/release-body-5.0.md`, update the "Current version" line in `CLAUDE.md` and the Scoop bucket note.
4. **Release by hand** on GitHub: run a plain `BUILD.bat` (both arches), upload `MasselGUARD-x64.zip`, `MasselGUARD-arm64.zip` **and their `.zip.sha256` files**; the in-app updater refuses a release without the matching `.sha256`. Then bump the Scoop bucket (`../MasselGUARD-scoop`: version + both hashes, which are the same sha256 values as the `.sha256` files) and run `MasselGUARDcli check-update` once.
5. **Wording check** of the strings added for the service and the bypass: written by the assistant, never reviewed by a native speaker (the accents/umlauts in de, fr, es, it, pl, pt-BR, tr and nl were corrected after a first accent-less draft).

6. **DNS server list**: (a) check every entry of the dnslist repo against the provider's own documentation (addresses, DoH templates, `blocks`, `logging`, names, privacy-policy links; Mullvad and CleanBrowsing and the family/unfiltered variants were written from memory and are the least sure), (b) make sure `main` of `masselink/MasselGUARD-dnslist` holds `index.json` and `servers/*.json` (the app downloads them from `raw.githubusercontent.com/.../main/`; before that the picker is empty and says so), turn on branch protection and required review there (there is no checksum or validation workflow, so a bad merge reaches users within a day; the app skips invalid entries but cannot judge a wrong address), (c) try the picker in a build: add/remove, the NextDNS field, the speed test (also with only ticked servers), a language switch.

Still open, not blocking (see the Security doc, "Functional items still open"): automatic fallback to direct mode when the service stops, a GUI list for allowed users, English-only RPC error texts.

## 3. Rules of engagement (from the user's standing instructions)

- **Never `git commit`** or push unless asked; the user commits. Never run the Debug `MasselGUARD.exe` (it self-installs into Program Files and breaks the installed copy). The user runs `BUILD.bat x64 nozip run` himself to test; verify with the two `dotnet build` commands and `MasselGUARDcli selftest` from `MasselGUARDcli\bin\Debug\...`.
- **No em-dashes** in any text (use a hyphen). **All 12 language files stay in key parity**; every user-facing string goes through `Lang.T`. Activity-log lines, the CLI and rule/DNS reasons stay English (see `CLAUDE.md`, Localization rules).
- Shared `Services/*.cs` that the CLI needs must be listed in `MasselGUARDcli/MasselGUARDcli.csproj` (`Models/*.cs` is globbed). Keep shared files WPF-free.
- Put pure logic in testable functions and add `RunSelfTest` cases; wire new suites into `Cli/CliRunner.cs` `CmdSelfTest` (and the counts in `docs/CliSelfTest.md`).
- Docs to keep in step: `CLAUDE.md`, `docs/MANUAL.md`, `docs/CLIManual.md`, `docs/WHATSNEW.md`, `docs/CliSelfTest.md`, the README, and for the service `docs/ServiceBackend-Design.md` + `-Security.md`.

## 4. Traps found the hard way

- **Script encoding:** PowerShell 5.1 (`powershell.exe`, and bash heredocs piped to it) reads a BOM-less script as ANSI, so emoji and non-ASCII come out as mojibake. Write helper scripts with the Write tool and run them with pwsh 7. After any scripted edit, grep for `â`/`Ã`.
- **Backslashes in bash heredocs and `sed` are halved by the tool layer**; paths and regexes in scripts must be written with the Write tool (a PowerShell here-string `@' ... '@` is literal: a doubled `''` stays doubled).
- **`sed -i` on a `.bat`** can convert CRLF to LF and break `BUILD.bat` (cmd mis-parses labels). Edit batch files with the Edit tool; inside a parenthesised block write `endlocal & exit /b 1`, not `^&`.
- **A file locked by a viewer** ("user-mapped section open") makes a scripted write fail; retry or use `sed`.
- **`Lang.T` has a `params` overload**, so a method group does not convert to `Func<string,string>`; use a lambda.
- **Pipes:** do not `using` a `StreamWriter` and `StreamReader` over the same pipe (use `leaveOpen`); never write to a pipe without a timeout on either side (a client that stops reading deadlocked the server).
- **`ServiceBase.ServiceName`** clashes with a same-named constant (`ServiceHost.SvcName`).
- **Service data folders** must be created with `SecureFolders.Ensure` (owner + DACL), never `Directory.CreateDirectory`; the tunnel store folder must not inherit (machine-scope DPAPI).
- A `GridSplitter` next to a zero-width column does nothing (see the column bullets in `CLAUDE.md`); every resize `Thumb` needs `Style="{StaticResource HandleThumb}"`. Menus opened with `FetchMenu.Show` need `atMouse: true` for right-click menus.

## 5. Other open items (not part of 5.0)

- **Pause automation for N minutes** and phase 2 of **create a rule from this network** (toast button, tray item), both specified in `docs/AutomationShortcuts-Design.md`.
- Ideas not started: time/weekday conditions in network rules, a shadowed-rule warning, a Chromium extension for one-click link bypass (it would talk to the same service endpoint; Chrome's Secure DNS and DNS cache limit it), code-signing the releases (would replace the checksum with a real signature), OpenVPN support (large; not planned).
