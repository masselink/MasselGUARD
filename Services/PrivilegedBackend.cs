using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Picks the privileged back-end once at startup (docs/ServiceBackend-Design.md decision 9):
    /// the MasselGUARD service when it is installed and answering, otherwise today's in-process
    /// "direct mode" (the app itself is elevated). GUI-side only.
    /// </summary>
    public sealed class PrivilegedBackend
    {
        public bool IsService { get; }
        public ITunnelOps Tunnels { get; }
        public IKillSwitchOps KillSwitch { get; }
        public IDnsOps Dns { get; }
        /// <summary>Timed-DNS-override hold kept by the service; null in direct mode.</summary>
        public IDnsHoldOps? Hold { get; }

        private PrivilegedBackend(bool isService, ITunnelOps t, IKillSwitchOps k, IDnsOps d, IDnsHoldOps? hold = null)
        { IsService = isService; Tunnels = t; KillSwitch = k; Dns = d; Hold = hold; }

        public static PrivilegedBackend Select(LogService log)
        {
            try
            {
                var rpc = new RpcOps(expectedServerPid: ServiceInstaller.ServicePid);
                if (ServiceInstaller.IsInstalled() && rpc.IsAvailable())
                {
                    log.Info("Back-end: MasselGUARD service");
                    return new PrivilegedBackend(true, rpc, rpc, rpc, rpc);
                }
            }
            catch { /* fall through to direct mode */ }
            log.Debug("Back-end: direct (in-process)");
            return new PrivilegedBackend(false, new TunnelDllOps(), new KillSwitchService(log), new DnsService(log));
        }

        /// <summary>Stops every local tunnel this back-end runs (app exit). In direct mode that is what
        /// <c>TunnelDll.DisconnectAll</c> already did; through the service the tunnels were started by the
        /// service process, so they are looked up by name.</summary>
        public void DisconnectAll(IEnumerable<StoredTunnel> tunnels)
        {
            if (!IsService) { TunnelDll.DisconnectAll(); return; }
            foreach (var t in tunnels.Where(t => string.IsNullOrEmpty(t.Source) || t.Source == "local"))
                try { if (Tunnels.IsRunning(t.Name)) Tunnels.Disconnect(t.Name, out _); } catch { }
        }
    }
}
