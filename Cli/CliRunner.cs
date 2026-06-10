using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Cli
{
    /// <summary>
    /// CLI entry point. Invoked from Program.Main when command-line args are present.
    ///
    /// Runs entirely without WPF — no App, no MainWindow.
    /// Coexists safely with a running GUI instance: both talk to the same WireGuard
    /// kernel driver; the GUI picks up state changes within ~1 second via its poll timer.
    ///
    /// Commands
    ///   list   / --list              All tunnels + connected/idle status
    ///   status / --status            Active tunnel count and names
    ///   connect  &lt;name&gt;              Connect a tunnel by name
    ///   connect  --default           Connect the configured default action tunnel
    ///   connect  --all               Connect all tunnels (optionally filtered by --group)
    ///   disconnect &lt;name&gt;            Disconnect a tunnel by name
    ///   disconnect-all               Disconnect all active tunnels
    ///   info &lt;name&gt;                  Detailed status for one tunnel
    ///   log [n]                      Recent activity log entries (default 20)
    ///   tunnel-history [n]       Connection history (default 20)
    ///   wifi-history [n]             WiFi SSID history (default 20)
    ///   import &lt;file&gt;               Import a .conf or .conf.dpapi tunnel
    ///   delete &lt;name&gt;               Remove a tunnel from config
    ///   rawconnect                   Connect a tunnel built from inline parameters
    ///   check-update                 Check GitHub for a newer version
    ///   version / --version / -v     Version, build, author + update status
    ///   help    / --help    / -h     Command reference
    ///
    /// Flags (any command)
    ///   --json                       Machine-readable JSON output
    ///   --quiet / -q                 No output — exit code only
    ///   --group &lt;name&gt;               Scope list / connect --all / disconnect-all to one group
    ///   --active                     Filter list to connected tunnels only
    ///   --logtype normal|extended    Log detail level (default: normal)
    ///
    /// Exit codes
    ///   0  success
    ///   1  error (tunnel not found, connect failed, not elevated, etc.)
    ///   2  already in desired state
    /// </summary>
    internal static class CliRunner
    {
        // ── Exe name (used in usage/help strings) ─────────────────────────────
        private static readonly string ExeName =
            System.IO.Path.GetFileNameWithoutExtension(
                Environment.ProcessPath ?? "MasselGUARDcli");

        // ── Public API ────────────────────────────────────────────────────────

        public static bool IsCliInvocation(string[] args)
        {
            if (args.Length == 0) return false;
            return !string.Equals(args[0], "/service", StringComparison.OrdinalIgnoreCase);
        }

        public static int Run(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // ── Global flags ──────────────────────────────────────────────────
            bool json   = args.Any(a => string.Equals(a, "--json",   StringComparison.OrdinalIgnoreCase));
            bool quiet  = args.Any(a => string.Equals(a, "--quiet",  StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(a, "-q",       StringComparison.OrdinalIgnoreCase));
            bool active = args.Any(a => string.Equals(a, "--active", StringComparison.OrdinalIgnoreCase));

            string? group = ParseFlagValue(args, "--group");

            string logType = ParseFlagValue(args, "--logtype") ?? "normal";

            string cmd = args[0].ToLowerInvariant();

            // ── Commands needing no config ─────────────────────────────────────
            if (cmd is "help" or "--help" or "-h" or "-?")
                return CmdHelp();

            // ── Load config ───────────────────────────────────────────────────
            var configSvc = new ConfigService();
            configSvc.Load();
            var cfg = configSvc.Config;

            int exitCode;
            try
            {
                exitCode = cmd switch
                {
                    "list"          or "--list"        => CmdList(cfg, json, group, active),
                    "status"        or "--status"       => CmdStatus(cfg, json),
                    "connect"                           => CmdConnect(args, cfg, json, quiet, group),
                    "disconnect"                        => CmdDisconnect(args, cfg, json, quiet),
                    "disconnect-all"                    => CmdDisconnectAll(cfg, json, quiet, group),
                    "info"                              => CmdInfo(args, cfg, json),
                    "log"                               => CmdLog(args, json, logType),
                    "tunnel-history"                => CmdTunnelHistory(args, json),
                    "wifi-history"                      => CmdWifiHistory(args, json),
                    "import"                            => CmdImport(args, cfg, configSvc, json, quiet),
                    "delete"        or "remove"         => CmdDelete(args, cfg, configSvc, json, quiet),
                    "rawconnect"                        => CmdRawConnect(args, cfg, configSvc, json, quiet),
                    "check-update"  or "--check-update" => CmdCheckUpdate(cfg, () => configSvc.Save(), json, quiet),
                    "version"       or "--version"
                                    or "-v"             => CmdVersion(cfg, json),
                    _ => UnknownCmd(cmd),
                };
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Unexpected error: {ex.Message}");
                exitCode = 1;
            }

            Console.Out.Flush();
            return exitCode;
        }

        // ── list ──────────────────────────────────────────────────────────────

        private static int CmdList(AppConfig cfg, bool json, string? group, bool activeOnly)
        {
            var tunnels = cfg.Tunnels.AsEnumerable();

            if (!string.IsNullOrEmpty(group))
                tunnels = tunnels.Where(t =>
                    string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase));

            var rows = tunnels
                .Select(t => (t.Name, Active: IsActive(t), t.Source, t.Group))
                .ToList();

            if (activeOnly)
                rows = rows.Where(r => r.Active).ToList();

            if (json)
            {
                CliOutput.PrintJson(rows.Select(t => new
                {
                    name   = t.Name,
                    status = t.Active ? "connected" : "idle",
                    source = t.Source,
                    group  = string.IsNullOrEmpty(t.Group) ? null : (string?)t.Group,
                }));
            }
            else if (rows.Count == 0)
                CliOutput.Info(activeOnly ? "No active tunnels." : "No tunnels found.");
            else
                CliOutput.PrintTunnelTable(rows);

            return 0;
        }

        // ── status ────────────────────────────────────────────────────────────

        private static int CmdStatus(AppConfig cfg, bool json)
        {
            var active = cfg.Tunnels.Where(t => IsActive(t)).ToList();

            if (json)
            {
                CliOutput.PrintJson(new
                {
                    active_count = active.Count,
                    tunnels      = active.Select(t => t.Name).ToArray(),
                });
            }
            else if (active.Count == 0)
                CliOutput.Info("No active tunnels.");
            else
            {
                CliOutput.Info($"Active: {active.Count}");
                foreach (var t in active)
                    CliOutput.Info($"  • {t.Name}");
            }
            return 0;
        }

        // ── connect ───────────────────────────────────────────────────────────

        private static int CmdConnect(string[] args, AppConfig cfg, bool json, bool quiet, string? group)
        {
            bool useDefault = args.Any(a => string.Equals(a, "--default", StringComparison.OrdinalIgnoreCase));
            bool useAll     = args.Any(a => string.Equals(a, "--all",     StringComparison.OrdinalIgnoreCase));

            if (useAll) return CmdConnectAll(cfg, json, quiet, group);

            StoredTunnel? tunnel;

            if (useDefault)
            {
                if (string.IsNullOrEmpty(cfg.DefaultTunnel))
                {
                    PrintResult(quiet, json, "error", "No default tunnel is configured.");
                    return 1;
                }
                tunnel = FindTunnel(cfg, cfg.DefaultTunnel);
                if (tunnel == null)
                {
                    PrintResult(quiet, json, "error",
                        $"Default tunnel '{cfg.DefaultTunnel}' not found in config.");
                    return 1;
                }
            }
            else
            {
                var name = args.Skip(1).FirstOrDefault(a =>
                    !a.StartsWith("--", StringComparison.Ordinal));
                if (name == null)
                {
                    CliOutput.Error($"Usage: {ExeName} connect <name>    [options]");
                    CliOutput.Error($"       {ExeName} connect --default  [options]");
                    CliOutput.Error($"       {ExeName} connect --all      [--group <name>] [options]");
                    return 1;
                }
                tunnel = FindTunnel(cfg, name);
                if (tunnel == null)
                {
                    PrintResult(quiet, json, "error", $"Tunnel '{name}' not found.");
                    return 1;
                }
            }

            // ── Override fields (Case B) ──────────────────────────────────────
            var overrides = ParseOverrides(args);
            if (overrides.Count > 0)
            {
                var plain = TunnelService.DecryptConfig(tunnel);
                if (string.IsNullOrEmpty(plain))
                {
                    PrintResult(quiet, json, "error",
                        $"Cannot read config for '{tunnel.Name}' — override not possible.");
                    return 1;
                }
                var patched = WireGuardConf.Patch(plain, overrides);
                // Work on a shallow clone so we don't mutate the stored tunnel
                tunnel = new StoredTunnel
                {
                    Name                = tunnel.Name,
                    Source              = tunnel.Source,
                    Group               = tunnel.Group,
                    Notes               = tunnel.Notes,
                    Config              = TunnelService.EncryptConfig(patched),
                    Path                = null,   // use inline config
                    KillSwitch          = tunnel.KillSwitch,
                    PreConnectScript    = tunnel.PreConnectScript,
                    PostConnectScript   = tunnel.PostConnectScript,
                    PreDisconnectScript = tunnel.PreDisconnectScript,
                    PostDisconnectScript= tunnel.PostDisconnectScript,
                };
            }

            if (IsActive(tunnel))
            {
                PrintResult(quiet, json, "already_connected",
                    $"Tunnel '{tunnel.Name}' is already connected.");
                return 2;
            }

            // For companion tunnels the service may need to be registered before
            // starting, which involves SCM calls and WaitForStatus polling (up to 15 s).
            // Print a progress line so the console stays visibly active.
            bool isCompanion = !string.Equals(tunnel.Source, "local",
                StringComparison.OrdinalIgnoreCase);
            if (isCompanion && !quiet && !json)
                CliOutput.Info($"Connecting '{tunnel.Name}' (WireGuard companion)…");

            bool ok = MakeTunnelService().Connect(tunnel, cfg, "CLI");
            if (ok)
                PrintResult(quiet, json, "connected", $"Tunnel '{tunnel.Name}' connected.");
            else
                PrintResult(quiet, json, "error", $"Failed to connect tunnel '{tunnel.Name}'.");

            return ok ? 0 : 1;
        }

        // ── connect --all ─────────────────────────────────────────────────────

        private static int CmdConnectAll(AppConfig cfg, bool json, bool quiet, string? group)
        {
            var tunnels = cfg.Tunnels.AsEnumerable();
            if (!string.IsNullOrEmpty(group))
                tunnels = tunnels.Where(t =>
                    string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase));

            var toConnect = tunnels.Where(t => !IsActive(t)).ToList();

            if (toConnect.Count == 0)
            {
                PrintResult(quiet, json, "already_connected",
                    group == null ? "All tunnels are already connected."
                                  : $"All tunnels in group '{group}' are already connected.");
                return 2;
            }

            var svc       = MakeTunnelService();
            var connected = new List<string>();
            var failed    = new List<string>();

            foreach (var t in toConnect)
                (svc.Connect(t, cfg, "CLI") ? connected : failed).Add(t.Name);

            if (json)
                CliOutput.PrintJson(new { connected = connected.ToArray(), failed = failed.ToArray() });
            else if (!quiet)
            {
                if (connected.Count > 0)
                    CliOutput.Ok($"Connected {connected.Count} tunnel(s): {string.Join(", ", connected)}");
                if (failed.Count > 0)
                    CliOutput.Error($"Failed: {string.Join(", ", failed)}");
            }

            return failed.Count == 0 ? 0 : 1;
        }

        // ── disconnect ────────────────────────────────────────────────────────

        private static int CmdDisconnect(string[] args, AppConfig cfg, bool json, bool quiet)
        {
            var name = args.Skip(1).FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal));
            if (name == null)
            {
                CliOutput.Error($"Usage: {ExeName} disconnect <name> [--json] [--quiet]");
                return 1;
            }

            var tunnel = FindTunnel(cfg, name);
            if (tunnel == null)
            {
                PrintResult(quiet, json, "error", $"Tunnel '{name}' not found.");
                return 1;
            }

            if (!IsActive(tunnel))
            {
                PrintResult(quiet, json, "already_disconnected",
                    $"Tunnel '{tunnel.Name}' is already disconnected.");
                return 2;
            }

            MakeTunnelService().Disconnect(tunnel);
            PrintResult(quiet, json, "disconnected", $"Tunnel '{tunnel.Name}' disconnected.");
            return 0;
        }

        // ── disconnect-all ────────────────────────────────────────────────────

        private static int CmdDisconnectAll(AppConfig cfg, bool json, bool quiet, string? group)
        {
            var tunnels = cfg.Tunnels.AsEnumerable();
            if (!string.IsNullOrEmpty(group))
                tunnels = tunnels.Where(t =>
                    string.Equals(t.Group, group, StringComparison.OrdinalIgnoreCase));

            var active = tunnels.Where(t => IsActive(t)).ToList();

            if (active.Count == 0)
            {
                PrintResult(quiet, json, "already_disconnected",
                    group == null ? "No active tunnels."
                                  : $"No active tunnels in group '{group}'.");
                return 2;
            }

            var svc = MakeTunnelService();
            foreach (var t in active) svc.Disconnect(t);

            PrintResult(quiet, json, "disconnected",
                $"Disconnected {active.Count} tunnel(s).");
            return 0;
        }

        // ── info ──────────────────────────────────────────────────────────────

        private static int CmdInfo(string[] args, AppConfig cfg, bool json)
        {
            var name = args.Skip(1).FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal));
            if (name == null) { CliOutput.Error($"Usage: {ExeName} info <name> [--json]"); return 1; }

            var tunnel = FindTunnel(cfg, name);
            if (tunnel == null) { CliOutput.Error($"Tunnel '{name}' not found."); return 1; }

            bool isActive = IsActive(tunnel);

            var hist = new HistoryService();
            hist.Load();

            var openSession = hist.Entries.FirstOrDefault(e =>
                e.TunnelName.Equals(tunnel.Name, StringComparison.OrdinalIgnoreCase)
                && e.DisconnectedAt == null);

            var lastSession = hist.Entries.FirstOrDefault(e =>
                e.TunnelName.Equals(tunnel.Name, StringComparison.OrdinalIgnoreCase));

            TimeSpan? uptime = isActive && openSession != null
                ? DateTime.UtcNow - openSession.ConnectedAt : (TimeSpan?)null;

            if (json)
            {
                CliOutput.PrintJson(new
                {
                    name           = tunnel.Name,
                    status         = isActive ? "connected" : "idle",
                    type           = tunnel.Source,
                    group          = string.IsNullOrEmpty(tunnel.Group) ? null : (string?)tunnel.Group,
                    uptime_sec     = uptime.HasValue ? (int?)((int)uptime.Value.TotalSeconds) : null,
                    last_source    = lastSession?.Source,
                    last_connected = lastSession?.ConnectedAt.ToLocalTime().ToString("o"),
                });
            }
            else
            {
                CliOutput.Info($"  Name:    {tunnel.Name}");
                CliOutput.Info($"  Type:    {(tunnel.Source == "local" ? "Local (tunnel.dll)" : "WireGuard for Windows")}");
                CliOutput.Info($"  Group:   {(string.IsNullOrEmpty(tunnel.Group) ? "—" : tunnel.Group)}");
                CliOutput.Info($"  Status:  {(isActive ? $"● Connected  {(uptime.HasValue ? FormatUptime(uptime.Value) : "unknown")}" : "○ Disconnected")}");

                if (lastSession != null)
                {
                    var when = FormatWhen(lastSession.ConnectedAt.ToLocalTime());
                    var src  = string.IsNullOrEmpty(lastSession.Source) ? "Manual" : lastSession.Source;
                    if (isActive)
                        CliOutput.Info($"  Source:  {src}  ({when})");
                    else
                    {
                        var dur = lastSession.DisconnectedAt.HasValue
                            ? FormatUptime(lastSession.DisconnectedAt.Value - lastSession.ConnectedAt) : "—";
                        CliOutput.Info($"  Last:    {when}  —  {dur}  ({src})");
                    }
                }
            }
            return 0;
        }

        // ── log ───────────────────────────────────────────────────────────────

        private static int CmdLog(string[] args, bool json, string logType)
        {
            int n = 20;
            var nArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            if (nArg != null) int.TryParse(nArg, out n);
            if (n <= 0) n = 20;

            bool extended = string.Equals(logType, "extended", StringComparison.OrdinalIgnoreCase);

            var hist = new HistoryService();
            hist.Load();
            var entries = hist.Entries.Take(n).ToList();

            if (json)
            {
                CliOutput.PrintJson(entries.Select(e => new
                {
                    tunnel          = e.TunnelName,
                    connected_at    = e.ConnectedAt.ToLocalTime().ToString("o"),
                    disconnected_at = e.DisconnectedAt?.ToLocalTime().ToString("o"),
                    duration_sec    = e.DisconnectedAt.HasValue
                        ? (int?)(int)(e.DisconnectedAt.Value - e.ConnectedAt).TotalSeconds : null,
                    active = e.DisconnectedAt == null,
                    source = extended ? e.Source : null,
                }));
                return 0;
            }

            if (entries.Count == 0) { CliOutput.Info("No history entries found."); return 0; }

            int nameW = Math.Max(entries.Max(e => e.TunnelName.Length), 6);
            int whenW = 18, durW = 10;

            CliOutput.Info(extended
                ? $"  {"Tunnel".PadRight(nameW)}  {"When".PadRight(whenW)}  {"Duration".PadRight(durW)}  Source"
                : $"  {"Tunnel".PadRight(nameW)}  {"When".PadRight(whenW)}  Duration");
            CliOutput.Info($"  {new string('─', nameW)}  {new string('─', whenW)}  {new string('─', durW)}{(extended ? "  ──────────────────" : "")}");

            foreach (var e in entries)
            {
                var when = FormatWhen(e.ConnectedAt.ToLocalTime()).PadRight(whenW);
                var dur  = e.DisconnectedAt.HasValue
                    ? FormatUptime(e.DisconnectedAt.Value - e.ConnectedAt).PadRight(durW)
                    : "active    ";

                if (extended)
                    CliOutput.Info($"  {e.TunnelName.PadRight(nameW)}  {when}  {dur}  {(string.IsNullOrEmpty(e.Source) ? "Manual" : e.Source)}");
                else
                    CliOutput.Info($"  {e.TunnelName.PadRight(nameW)}  {when}  {dur.TrimEnd()}");
            }
            return 0;
        }

        // ── tunnel-history ────────────────────────────────────────────────

        private static int CmdTunnelHistory(string[] args, bool json)
        {
            int n = 20;
            var nArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            if (nArg != null) int.TryParse(nArg, out n);
            if (n <= 0) n = 20;

            var hist = new HistoryService();
            hist.Load();
            var entries = hist.Entries.Take(n).ToList();

            if (json)
            {
                CliOutput.PrintJson(entries.Select(e => new
                {
                    tunnel          = e.TunnelName,
                    connected_at    = e.ConnectedAt.ToLocalTime().ToString("o"),
                    disconnected_at = e.DisconnectedAt?.ToLocalTime().ToString("o"),
                    duration_sec    = e.DisconnectedAt.HasValue
                        ? (int?)(int)(e.DisconnectedAt.Value - e.ConnectedAt).TotalSeconds : null,
                    active          = e.DisconnectedAt == null,
                    source          = e.Source,
                    rx_bytes        = e.SessionRxBytes,
                    tx_bytes        = e.SessionTxBytes,
                }));
                return 0;
            }

            if (entries.Count == 0) { CliOutput.Info("No connection history."); return 0; }

            int nameW = Math.Max(entries.Max(e => e.TunnelName.Length), 6);
            int whenW = 18, durW = 10;

            CliOutput.Info($"  {"Tunnel".PadRight(nameW)}  {"When".PadRight(whenW)}  {"Duration".PadRight(durW)}  Source");
            CliOutput.Info($"  {new string('─', nameW)}  {new string('─', whenW)}  {new string('─', durW)}  ──────────────────");

            foreach (var e in entries)
            {
                var when = FormatWhen(e.ConnectedAt.ToLocalTime()).PadRight(whenW);
                var dur  = e.DisconnectedAt.HasValue
                    ? FormatUptime(e.DisconnectedAt.Value - e.ConnectedAt).PadRight(durW)
                    : "active    ";
                var src  = string.IsNullOrEmpty(e.Source) ? "Manual" : e.Source;
                CliOutput.Info($"  {e.TunnelName.PadRight(nameW)}  {when}  {dur}  {src}");
            }
            return 0;
        }

        // ── wifi-history ──────────────────────────────────────────────────────

        private static int CmdWifiHistory(string[] args, bool json)
        {
            int n = 20;
            var nArg = args.Skip(1).FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
            if (nArg != null) int.TryParse(nArg, out n);
            if (n <= 0) n = 20;

            var hist = new HistoryService();
            hist.LoadSsid();
            var entries = hist.SsidEntries.Take(n).ToList();

            if (json)
            {
                CliOutput.PrintJson(entries.Select(e => new
                {
                    ssid            = e.Ssid,
                    connected_at    = e.ConnectedAt.ToLocalTime().ToString("o"),
                    disconnected_at = e.DisconnectedAt?.ToLocalTime().ToString("o"),
                    duration_sec    = e.DisconnectedAt.HasValue
                        ? (int?)(int)(e.DisconnectedAt.Value - e.ConnectedAt).TotalSeconds : null,
                    active          = e.DisconnectedAt == null,
                    open            = e.IsOpen,
                }));
                return 0;
            }

            if (entries.Count == 0) { CliOutput.Info("No WiFi history."); return 0; }

            int ssidW = Math.Max(entries.Max(e => (e.Ssid ?? "").Length), 4);
            int whenW = 18, durW = 10;

            CliOutput.Info($"  {"SSID".PadRight(ssidW)}  {"When".PadRight(whenW)}  {"Duration".PadRight(durW)}  Security");
            CliOutput.Info($"  {new string('─', ssidW)}  {new string('─', whenW)}  {new string('─', durW)}  ────────");

            foreach (var e in entries)
            {
                var ssid = (e.Ssid ?? "").PadRight(ssidW);
                var when = FormatWhen(e.ConnectedAt.ToLocalTime()).PadRight(whenW);
                var dur  = e.DisconnectedAt.HasValue
                    ? FormatUptime(e.DisconnectedAt.Value - e.ConnectedAt).PadRight(durW)
                    : "active    ";
                var sec  = e.IsOpen ? "open" : "secured";
                CliOutput.Info($"  {ssid}  {when}  {dur}  {sec}");
            }
            return 0;
        }

        // ── import ────────────────────────────────────────────────────────────

        private static int CmdImport(string[] args, AppConfig cfg, ConfigService configSvc,
                                     bool json, bool quiet)
        {
            var filePath = args.Skip(1).FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal));
            if (filePath == null)
            {
                CliOutput.Error($"Usage: {ExeName} import <file.conf|file.conf.dpapi>");
                CliOutput.Error("       Options: --name <display-name>  --group <name>  --unsecure");
                return 1;
            }

            if (!File.Exists(filePath))
            {
                CliOutput.Error($"File not found: {filePath}");
                return 1;
            }

            bool unsecure = args.Any(a => string.Equals(a, "--unsecure", StringComparison.OrdinalIgnoreCase));
            string? nameOverride = ParseFlagValue(args, "--name");
            string? groupArg    = ParseFlagValue(args, "--group");

            // Derive tunnel name
            string tunnelName = nameOverride
                ?? Path.GetFileNameWithoutExtension(
                   Path.GetFileNameWithoutExtension(filePath)); // strips both .dpapi and .conf
            if (string.IsNullOrWhiteSpace(tunnelName))
                tunnelName = Path.GetFileNameWithoutExtension(filePath);

            // Duplicate check
            if (cfg.Tunnels.Any(t => string.Equals(t.Name, tunnelName, StringComparison.OrdinalIgnoreCase)))
            {
                PrintResult(quiet, json, "error",
                    $"A tunnel named '{tunnelName}' already exists. Use --name to import with a different name.");
                return 1;
            }

            // Read / decrypt input file
            string plainText;
            try
            {
                bool isDpapi = filePath.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase)
                            || filePath.EndsWith(".dpapi",      StringComparison.OrdinalIgnoreCase);
                if (isDpapi)
                {
                    var cipher = File.ReadAllBytes(filePath);

                    // Try CurrentUser scope first (MasselGUARD's own tunnel storage).
                    byte[]? plain = null;
                    try
                    {
                        plain = System.Security.Cryptography.ProtectedData.Unprotect(
                            cipher, null,
                            System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    }
                    catch { }

                    // Fall back to LocalMachine scope — WireGuard for Windows uses this.
                    if (plain == null)
                    {
                        try
                        {
                            plain = System.Security.Cryptography.ProtectedData.Unprotect(
                                cipher, null,
                                System.Security.Cryptography.DataProtectionScope.LocalMachine);
                        }
                        catch { }
                    }

                    if (plain == null)
                    {
                        CliOutput.Error($"Failed to decrypt '{filePath}': DPAPI decryption failed " +
                                        "(tried CurrentUser and LocalMachine scopes). " +
                                        "Run as Administrator and ensure the file was encrypted on this machine.");
                        return 1;
                    }

                    plainText = System.Text.Encoding.UTF8.GetString(plain);
                    // Strip UTF-8 BOM if present
                    if (plainText.Length > 0 && plainText[0] == '﻿')
                        plainText = plainText.Substring(1);
                }
                else
                {
                    plainText = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to read '{filePath}': {ex.Message}");
                return 1;
            }

            // Build StoredTunnel
            StoredTunnel stored;

            if (unsecure)
            {
                // Copy plaintext .conf to <exedir>\tunnels\<name>.conf
                var tunnelsDir = Path.Combine(AppContext.BaseDirectory, "tunnels");
                Directory.CreateDirectory(tunnelsDir);
                var destPath = Path.Combine(tunnelsDir, SanitizeName(tunnelName) + ".conf");

                try { File.WriteAllText(destPath, plainText, System.Text.Encoding.UTF8); }
                catch (Exception ex)
                {
                    CliOutput.Error($"Failed to write tunnel file: {ex.Message}");
                    return 1;
                }

                stored = new StoredTunnel
                {
                    Name   = tunnelName,
                    Source = "local",
                    Path   = destPath,
                    Group  = groupArg ?? "",
                };

                if (!quiet && !json)
                    CliOutput.Info("⚠  Stored without DPAPI encryption. The config is readable on disk.");
            }
            else
            {
                stored = new StoredTunnel
                {
                    Name   = tunnelName,
                    Source = "local",
                    Path   = TunnelService.SaveConfigToFile(tunnelName, plainText),
                    Group  = groupArg ?? "",
                };
            }

            cfg.Tunnels.Add(stored);
            configSvc.Save();

            PrintResult(quiet, json, "imported", $"Tunnel '{tunnelName}' imported successfully.");
            return 0;
        }

        // ── delete ────────────────────────────────────────────────────────────

        private static int CmdDelete(string[] args, AppConfig cfg, ConfigService configSvc,
                                     bool json, bool quiet)
        {
            var name = args.Skip(1).FirstOrDefault(a =>
                !a.StartsWith("--", StringComparison.Ordinal));
            if (name == null)
            {
                CliOutput.Error($"Usage: {ExeName} delete <name> [--force] [--json] [--quiet]");
                return 1;
            }

            var tunnel = FindTunnel(cfg, name);
            if (tunnel == null)
            {
                PrintResult(quiet, json, "error", $"Tunnel '{name}' not found.");
                return 1;
            }

            bool force = args.Any(a => string.Equals(a, "--force", StringComparison.OrdinalIgnoreCase));

            if (IsActive(tunnel))
            {
                if (!force)
                {
                    PrintResult(quiet, json, "error",
                        $"Tunnel '{tunnel.Name}' is active. Disconnect it first, or use --force.");
                    return 1;
                }
                MakeTunnelService().Disconnect(tunnel);
            }

            // Delete associated file if path-based
            if (!string.IsNullOrEmpty(tunnel.Path) && File.Exists(tunnel.Path))
            {
                try { File.Delete(tunnel.Path); }
                catch { /* non-critical */ }
            }

            cfg.Tunnels.Remove(tunnel);
            configSvc.Save();

            PrintResult(quiet, json, "deleted", $"Tunnel '{tunnel.Name}' deleted.");
            return 0;
        }

        // ── rawconnect ────────────────────────────────────────────────────────

        private static int CmdRawConnect(string[] args, AppConfig cfg, ConfigService configSvc,
                                         bool json, bool quiet)
        {
            // Required
            var endpoint = ParseFlagValue(args, "--endpoint");
            var pubkey   = ParseFlagValue(args, "--pubkey");

            // Private key — inline or file
            var privkeyRaw  = ParseFlagValue(args, "--privkey");
            var privkeyFile = ParseFlagValue(args, "--privkeyfile");

            // PSK — inline or file
            var pskRaw  = ParseFlagValue(args, "--psk");
            var pskFile = ParseFlagValue(args, "--pskfile");

            // Optional
            var address   = ParseFlagValue(args, "--address");
            var dns       = ParseFlagValue(args, "--dns");
            var allowed   = ParseFlagValue(args, "--allowed");
            var nameArg   = ParseFlagValue(args, "--name");
            var groupArg  = ParseFlagValue(args, "--group");
            bool save     = args.Any(a => string.Equals(a, "--save", StringComparison.OrdinalIgnoreCase));

            // Validate required args
            if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(pubkey)
                || (string.IsNullOrEmpty(privkeyRaw) && string.IsNullOrEmpty(privkeyFile)))
            {
                CliOutput.Error($"Usage: {ExeName} rawconnect --endpoint <host:port> --pubkey <key>");
                CliOutput.Error("                        (--privkey <key> | --privkeyfile <path>)");
                CliOutput.Error("                        [--address <CIDR>] [--dns <servers>] [--allowed <CIDRs>]");
                CliOutput.Error("                        [--psk <key> | --pskfile <path>]");
                CliOutput.Error("                        [--name <display>] [--group <name>] [--save]");
                return 1;
            }

            // Resolve private key
            string privkey;
            if (!string.IsNullOrEmpty(privkeyFile))
            {
                if (!File.Exists(privkeyFile))
                {
                    CliOutput.Error($"Private key file not found: {privkeyFile}");
                    return 1;
                }
                privkey = File.ReadAllText(privkeyFile).Trim();
            }
            else
            {
                privkey = privkeyRaw!;
                if (!quiet && !json)
                    CliOutput.Error(
                        "⚠  WARNING: private key is visible in process listings and shell history.\n" +
                        "   Use --privkeyfile <path> to load it from a file instead.");
            }

            // Resolve PSK
            string? psk = null;
            if (!string.IsNullOrEmpty(pskFile))
            {
                if (!File.Exists(pskFile)) { CliOutput.Error($"PSK file not found: {pskFile}"); return 1; }
                psk = File.ReadAllText(pskFile).Trim();
            }
            else if (!string.IsNullOrEmpty(pskRaw))
            {
                psk = pskRaw;
            }

            // Build the conf text
            string confText;
            try
            {
                confText = WireGuardConf.Build(
                    privateKey:   privkey,
                    publicKey:    pubkey,
                    endpoint:     endpoint,
                    address:      address,
                    dns:          dns,
                    allowedIPs:   allowed,
                    presharedKey: psk);
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to build config: {ex.Message}");
                return 1;
            }

            var displayName = !string.IsNullOrWhiteSpace(nameArg)
                ? nameArg
                : $"rawconnect-{DateTime.Now:yyMMddHHmm}";

            var stored = new StoredTunnel
            {
                Name   = displayName,
                Source = "local",
                Path   = TunnelService.SaveConfigToFile(displayName, confText),
                Group  = groupArg ?? "",
            };

            // Optionally persist
            if (save)
            {
                if (cfg.Tunnels.Any(t => string.Equals(t.Name, displayName, StringComparison.OrdinalIgnoreCase)))
                {
                    PrintResult(quiet, json, "error",
                        $"A tunnel named '{displayName}' already exists. Use --name to choose a different name.");
                    return 1;
                }
                cfg.Tunnels.Add(stored);
                configSvc.Save();
                if (!quiet && !json)
                    CliOutput.Info($"Tunnel '{displayName}' saved to config.");
            }

            if (IsActive(stored))
            {
                PrintResult(quiet, json, "already_connected",
                    $"Tunnel '{displayName}' is already connected.");
                return 2;
            }

            bool ok = MakeTunnelService().Connect(stored, cfg, "CLI");
            if (ok)
                PrintResult(quiet, json, "connected", $"Tunnel '{displayName}' connected.");
            else
                PrintResult(quiet, json, "error", $"Failed to connect tunnel '{displayName}'.");

            return ok ? 0 : 1;
        }

        // ── check-update ──────────────────────────────────────────────────────

        private static int CmdCheckUpdate(AppConfig cfg, Action saveConfig, bool json, bool quiet)
        {
            if (!quiet && !json) CliOutput.Info("Checking for updates...");

            ReleaseInfo? latest = null;
            string error = "";
            try
            {
                latest = System.Threading.Tasks.Task
                    .Run(() => UpdateChecker.CheckNowAsync(cfg, saveConfig))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex) { error = ex.Message; }

            if (!string.IsNullOrEmpty(error))
            {
                PrintResult(quiet, json, "error", $"Update check failed: {error}");
                return 1;
            }

            if (latest == null)
            {
                PrintResult(quiet, json, "unknown", "No release information available.");
                return 1;
            }

            var current     = UpdateChecker.CurrentVersionString;
            bool updateAvail = UpdateChecker.IsNewerVersion(latest.TagName);
            bool ahead       = UpdateChecker.IsAheadOfLatest(latest.TagName);

            string statusKey = updateAvail ? "update_available" : ahead ? "ahead" : "up_to_date";
            string statusMsg = updateAvail
                ? $"Update available: v{latest.TagName}  (current: v{current})"
                : ahead ? $"Running ahead of latest release ({latest.TagName})."
                        : $"Up to date — v{current} is the latest release.";

            if (json)
                CliOutput.PrintJson(new { result = statusKey, current, latest = latest.TagName, message = statusMsg });
            else if (!quiet)
                CliOutput.Ok(statusMsg);

            return updateAvail ? 1 : 0;
        }

        // ── version ───────────────────────────────────────────────────────────

        private static int CmdVersion(AppConfig cfg, bool json)
        {
            var ver      = UpdateChecker.CurrentVersionString;
            var stamp    = UpdateChecker.BuildStamp;
            var codename = UpdateChecker.Codename;

            var    latestKnown = cfg.LatestKnownVersion;
            string updateStatus =
                string.IsNullOrEmpty(latestKnown) ? $"unknown — run '{ExeName} check-update'" :
                UpdateChecker.IsNewerVersion(latestKnown) ? $"update available — v{latestKnown}" :
                UpdateChecker.IsAheadOfLatest(latestKnown) ? $"ahead of latest ({latestKnown})" :
                "up to date";

            if (json)
            {
                CliOutput.PrintJson(new
                {
                    version       = ver,
                    codename      = string.IsNullOrEmpty(codename) ? null : (string?)codename,
                    build         = string.IsNullOrEmpty(stamp)    ? null : (string?)stamp,
                    update_status = updateStatus,
                });
            }
            else
            {
                CliOutput.Info(string.IsNullOrEmpty(codename)
                    ? $"{ExeName} v{ver}"
                    : $"{ExeName} v{ver}  |  {codename}");
                if (!string.IsNullOrEmpty(stamp)) CliOutput.Info($"build:   {stamp}");
                CliOutput.Info("Harold Masselink  |  https://masselink.net");
                CliOutput.Info($"Update:  {updateStatus}");
            }
            return 0;
        }

        // ── help ──────────────────────────────────────────────────────────────

        private static int CmdHelp()
        {
            CliOutput.Info($"{ExeName} — MasselGUARD command-line interface");
            CliOutput.Info("");
            CliOutput.Info("Usage:");
            CliOutput.Info($"  {ExeName} <command> [options]");
            CliOutput.Info("");
            CliOutput.Info("Commands:");
            CliOutput.Info("  list,    --list            List all tunnels and their status");
            CliOutput.Info("  status,  --status          Show active tunnel count and names");
            CliOutput.Info("  connect <name>             Connect a tunnel by name");
            CliOutput.Info("  connect --default          Connect the configured default tunnel");
            CliOutput.Info("  connect --all              Connect all tunnels");
            CliOutput.Info("  disconnect <name>          Disconnect a tunnel by name");
            CliOutput.Info("  disconnect-all             Disconnect all active tunnels");
            CliOutput.Info("  info <name>                Detailed status for one tunnel");
            CliOutput.Info("  log [n]                    Last n activity log entries (default 20)");
            CliOutput.Info("  tunnel-history [n]     Connection history with source and traffic (default 20)");
            CliOutput.Info("  wifi-history [n]           WiFi SSID history with duration and security (default 20)");
            CliOutput.Info("  import <file>              Import a .conf or .conf.dpapi tunnel");
            CliOutput.Info("  delete <name>              Remove a tunnel from config");
            CliOutput.Info("  rawconnect                 Connect a tunnel built from inline parameters");
            CliOutput.Info("  check-update               Check GitHub for a newer version");
            CliOutput.Info("  version, --version, -v     Show version, build and update status");
            CliOutput.Info("  help,    --help,    -h     Show this help");
            CliOutput.Info("");
            CliOutput.Info("Options (any command):");
            CliOutput.Info("  --json                     Output in JSON format");
            CliOutput.Info("  --quiet, -q                No output — exit code only");
            CliOutput.Info("  --group <name>             Scope list / connect --all / disconnect-all");
            CliOutput.Info("  --active                   Filter list to connected tunnels only");
            CliOutput.Info("  --logtype normal|extended  Log detail level (default: normal)");
            CliOutput.Info("");
            CliOutput.Info("connect overrides (patch a stored config before connecting):");
            CliOutput.Info("  --override-dns <servers>   Override DNS server(s)");
            CliOutput.Info("  --override-endpoint <h:p>  Override server endpoint");
            CliOutput.Info("  --override-address <CIDR>  Override interface address");
            CliOutput.Info("");
            CliOutput.Info("import options:");
            CliOutput.Info("  --name <display-name>      Display name (default: filename)");
            CliOutput.Info("  --unsecure                 Store without DPAPI encryption");
            CliOutput.Info("");
            CliOutput.Info("delete options:");
            CliOutput.Info("  --force                    Disconnect first if active");
            CliOutput.Info("");
            CliOutput.Info("rawconnect options:");
            CliOutput.Info("  --endpoint <host:port>     Server endpoint  (required)");
            CliOutput.Info("  --pubkey <key>             Server public key  (required)");
            CliOutput.Info("  --privkey <key>            Client private key (WARNING! visible in ps/console)");
            CliOutput.Info("  --privkeyfile <path>       Client private key from file (safer)");
            CliOutput.Info("  --address <CIDR>           Interface address");
            CliOutput.Info("  --dns <servers>            DNS servers");
            CliOutput.Info("  --psk <key>                Pre-shared key (inline)");
            CliOutput.Info("  --pskfile <path>           Pre-shared key from file");
            CliOutput.Info("  --allowed <CIDRs>          Allowed IPs (default: 0.0.0.0/0, ::/0)");
            CliOutput.Info("  --name <display>           Display name for this session");
            CliOutput.Info("  --save                     Import permanently before connecting");
            CliOutput.Info("");
            CliOutput.Info("Exit codes: 0 success  1 error  2 already in desired state");
            CliOutput.Info("Coexists safely with the GUI.");
            return 0;
        }

        // ── unknown command ───────────────────────────────────────────────────

        private static int UnknownCmd(string cmd)
        {
            CliOutput.Error($"Unknown command: '{cmd}'. Run '{ExeName} help' for a list of commands.");
            return 1;
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static bool IsActive(StoredTunnel tunnel)
        {
            if (string.Equals(tunnel.Source, "local", StringComparison.OrdinalIgnoreCase))
                return TunnelDll.IsRunning(tunnel.Name);
            try
            {
                using var sc = new ServiceController("WireGuardTunnel$" + tunnel.Name);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        private static StoredTunnel? FindTunnel(AppConfig cfg, string name)
            => cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        private static TunnelService MakeTunnelService()
            => new TunnelService(new LogService(), new ScriptService(), new HistoryService(), null);

        private static void PrintResult(bool quiet, bool json, string result, string message)
        {
            if (quiet) return;
            if (json)  { CliOutput.PrintJson(new { result, message }); return; }
            if (result == "error") CliOutput.Error(message);
            else                   CliOutput.Ok(message);
        }

        /// <summary>Returns the value after --flag, or null if the flag is absent.</summary>
        private static string? ParseFlagValue(string[] args, string flag)
        {
            var idx = Array.FindIndex(args,
                a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
        }

        /// <summary>
        /// Collects all --override-&lt;Field&gt; &lt;value&gt; pairs from args and
        /// returns them as a dictionary suitable for WireGuardConf.Patch().
        /// </summary>
        private static Dictionary<string, string> ParseOverrides(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].StartsWith("--override-", StringComparison.OrdinalIgnoreCase)) continue;
                var field = args[i]["--override-".Length..];  // e.g. "dns", "endpoint", "address"
                // Capitalise to match WireGuard field names (Dns → DNS, Endpoint → Endpoint)
                field = field.ToUpperInvariant() switch
                {
                    "DNS"      => "DNS",
                    "ENDPOINT" => "Endpoint",
                    "ADDRESS"  => "Address",
                    "PRIVKEY"  => "PrivateKey",
                    "ALLOWED"  => "AllowedIPs",
                    _          => System.Globalization.CultureInfo.InvariantCulture
                                       .TextInfo.ToTitleCase(field.ToLowerInvariant())
                };
                result[field] = args[i + 1];
            }
            return result;
        }

        private static string SanitizeName(string name)
            => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static string FormatUptime(TimeSpan t)
        {
            if (t.TotalSeconds < 60)  return $"{(int)t.TotalSeconds}s";
            if (t.TotalMinutes < 60)  return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
            if (t.TotalHours   < 24)  return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
            return                           $"{(int)t.TotalDays}d {t.Hours:D2}h {t.Minutes:D2}m";
        }

        private static string FormatWhen(DateTime local)
        {
            var today = DateTime.Now.Date;
            if (local.Date == today)               return $"today {local:HH:mm}";
            if (local.Date == today.AddDays(-1))   return $"yesterday {local:HH:mm}";
            if (local.Year == today.Year)          return local.ToString("dd MMM HH:mm");
            return                                        local.ToString("dd MMM yyyy");
        }
    }
}
