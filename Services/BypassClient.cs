using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// The client side of the DNS bypass, used by <c>MasselGUARDcli dns bypass</c> and by the Explorer right-click
    /// entries (which run <c>MasselGUARD.exe --bypass &lt;seconds|stop|toggle&gt;</c>: a windowless launcher, so no console
    /// window flashes). It first asks a running MasselGUARD window (<see cref="CommandPipe"/>), and when none answers it
    /// does the bypass through the MasselGUARD service. It runs as the normal user and never needs elevation.
    /// WPF-free and CLI-shared.
    /// </summary>
    public static class BypassClient
    {
        /// <summary>The request in the arguments of <c>--bypass [seconds|stop|toggle]</c> (args[0] is the switch).</summary>
        public static CommandPipe.BypassRequest ParseLauncherArgs(string[] args)
        {
            if (args.Length == 0 || !args[0].Equals("--bypass", StringComparison.OrdinalIgnoreCase))
                return new CommandPipe.BypassRequest("invalid", 0);
            var arg = args.Length > 1 ? args[1] : "";
            return CommandPipe.ParseBypass(("bypass " + arg).Trim());
        }

        /// <summary>Window first, then the service. Null = neither is available. The message starts with "ok" or "error".</summary>
        public static (bool ok, string message)? Run(CommandPipe.BypassRequest req, string pipeArg, AppConfig cfg)
        {
            var reply = CommandPipe.Send(("bypass " + pipeArg).Trim());
            if (reply != null) return (reply.StartsWith("ok", StringComparison.OrdinalIgnoreCase), reply);
            return ViaService(req, cfg);
        }

        /// <summary>No window is running: do the bypass through the MasselGUARD service. The caller runs as the user, so it
        /// can read the user's config (bypass profile, address families) and the current network adapters; the service
        /// only applies and holds what it is told, and ends it on time.</summary>
        public static (bool ok, string message)? ViaService(CommandPipe.BypassRequest req, AppConfig cfg)
        {
            if (!ServiceInstaller.IsInstalled()) return null;
            var rpc = new RpcOps(expectedServerPid: ServiceInstaller.ServicePid);
            if (!rpc.IsAvailable()) return null;

            string action = req.Action;
            if (action == "toggle") action = rpc.GetHold().active ? "stop" : "start";
            if (action == "stop")
                return rpc.StopHold() ? (true, "ok: DNS bypass stopped (through the MasselGUARD service).")
                                      : (true, "ok: no DNS bypass is running.");

            var snap = NetworkMonitor.Capture(cfg.PrimaryNetworkMode, null, resolveGatewayMac: false, wifiOnly: cfg.SimpleWifiMode);
            var guids = snap.Adapters.Select(a => Guid.TryParse(a.AdapterId, out var g) ? g : Guid.Empty).ToList();
            var plan = BypassPlan.Build(cfg, guids, req.Seconds);
            if (!plan.Ok) return (false, "error: " + plan.Error);

            int applied = 0;
            foreach (var g in plan.Interfaces)
                if (rpc.ApplyProfile(g, plan.Profile!, plan.Families)) applied++;
            if (applied == 0) return (false, "error: the service could not switch DNS.");
            if (!rpc.RegisterHold(plan.Profile!.Id, plan.Interfaces, plan.Seconds))
            {
                rpc.RestoreAll();
                return (false, "error: the service could not start the timer, DNS was put back.");
            }
            return (true, $"ok: using '{plan.Profile.Name}' for {plan.Seconds} seconds, then back to automatic (through the MasselGUARD service).");
        }

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }

            Check(ParseLauncherArgs(new[] { "--bypass", "60" }) is { Action: "start", Seconds: 60 }, "launcher: --bypass 60");
            Check(ParseLauncherArgs(new[] { "--bypass", "10" }) is { Action: "start", Seconds: 10 }, "launcher: --bypass 10");
            Check(ParseLauncherArgs(new[] { "--bypass", "stop" }) is { Action: "stop" }, "launcher: --bypass stop");
            Check(ParseLauncherArgs(new[] { "--bypass", "toggle" }) is { Action: "toggle" }, "launcher: --bypass toggle");
            Check(ParseLauncherArgs(new[] { "--bypass" }) is { Action: "toggle" }, "launcher: --bypass alone toggles");
            Check(ParseLauncherArgs(new[] { "--BYPASS", "Stop" }) is { Action: "stop" }, "launcher: case does not matter");
            Check(ParseLauncherArgs(new[] { "--bypass", "banana" }).Action == "invalid", "launcher: garbage is invalid");
            Check(ParseLauncherArgs(new[] { "--bypass", "10", "20" }) is { Action: "start", Seconds: 10 }, "launcher: extra arguments are ignored");
            Check(ParseLauncherArgs(new string[0]).Action == "invalid" && ParseLauncherArgs(new[] { "dns", "bypass" }).Action == "invalid", "launcher: other arguments are invalid");
            Check(ParseLauncherArgs(new[] { "--bypass", "999999" }) is { Action: "start", Seconds: CommandPipe.MaxSeconds }, "launcher: length clamped");
            return (pass, fails.Count, fails);
        }
    }
}
