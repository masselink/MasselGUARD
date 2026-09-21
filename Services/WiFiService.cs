using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MasselGUARD.Services
{
    /// <summary>
    /// WiFi monitoring via wlanapi.dll.
    /// Fires SsidChanged(ssid, isOpen) on every network change.
    /// On ACM_CONNECTED the SSID is read from the notification payload first;
    /// if that fails it retries WlanQueryInterface up to 5 × 200 ms.
    /// </summary>
    public class WiFiService : IDisposable
    {
        // ── P/Invoke ──────────────────────────────────────────────────────────
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(
            uint dwClientVersion, IntPtr pReserved,
            out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(
            IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")] private static extern uint WlanRegisterNotification(
            IntPtr hClientHandle, uint dwNotifSource, bool bIgnoreDuplicate,
            WlanNotifCallback funcCallback, IntPtr pCallbackContext,
            IntPtr pReserved, out uint pdwPrevNotifSource);

        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(
            IntPtr hClientHandle, ref Guid pInterfaceGuid, uint OpCode,
            IntPtr pReserved, out uint pdwDataSize, out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

        private delegate void WlanNotifCallback(IntPtr pData, IntPtr pCtx);

        // ── ACM notification codes ────────────────────────────────────────────
        private const uint WLAN_NOTIFICATION_SOURCE_ACM = 8;
        private const int  ACM_CONNECTED          = 9;
        private const int  ACM_DISCONNECTED        = 10;
        private const int  ACM_CONNECTION_COMPLETE = 20;
        private const uint WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;

        // ── WLAN_NOTIFICATION_DATA layout (offsets in bytes) ──────────────────
        // +0  NotificationSource (DWORD)
        // +4  NotificationCode   (DWORD)
        // +8  InterfaceGuid      (GUID, 16 bytes)
        // +24 dwDataSize         (DWORD)
        // +28 pData              (POINTER — points to WLAN_MSM_NOTIFICATION_DATA or similar)
        // For ACM_CONNECTED the pData pointer holds WLAN_CONNECTION_NOTIFICATION_DATA:
        //   +0  wlanConnectionMode (DWORD)
        //   +4  strProfileName[256] (512 bytes of WCHARs)
        //   +516 dot11Ssid:
        //     +516 uSSIDLength (DWORD)
        //     +520 ucSSID[32]
        //   +552 dot11BssType (DWORD)
        //   +556 bSecurityEnabled (BOOL)
        //   +560 wlanReasonCode (DWORD)
        // Total WLAN_CONNECTION_NOTIFICATION_DATA = 564 bytes

        // ── State ─────────────────────────────────────────────────────────────
        private IntPtr _handle = IntPtr.Zero;
        private WlanNotifCallback? _cb; // must be field — GC must not collect it
        private string? _lastFiredSsid = "##INIT##"; // sentinel so first event always fires

        public string? CurrentSsid   { get; private set; }
        public bool    IsOpenNetwork { get; private set; }
        /// <summary>GUID of the currently connected WLAN adapter (Guid.Empty when none).
        /// Used by DNS automation to target this exact interface.</summary>
        public Guid    CurrentInterfaceGuid { get; private set; }

        /// <summary>Fired on a thread-pool thread. Handlers must marshal to UI if needed.</summary>
        public event Action<string?, bool>? SsidChanged;

        // ── Start ─────────────────────────────────────────────────────────────
        public void Start()
        {
            if (_handle != IntPtr.Zero) return;
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0)
            {
                _handle = IntPtr.Zero;
                return;
            }
            _cb = OnNotification;
            WlanRegisterNotification(_handle, WLAN_NOTIFICATION_SOURCE_ACM,
                true, _cb, IntPtr.Zero, IntPtr.Zero, out _);
        }

        // ── Notification handler (runs on WLAN thread pool) ───────────────────
        private void OnNotification(IntPtr pData, IntPtr pCtx)
        {
            try
            {
                int code = Marshal.ReadInt32(pData, 4);

                if (code == ACM_DISCONNECTED)
                {
                    FireIfChanged(null, false);
                    return;
                }

                if (code is ACM_CONNECTED or ACM_CONNECTION_COMPLETE)
                {
                    string? ssid   = null;
                    bool    isOpen = false;
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(200);
                        var (q, o) = ReadCurrentSsidFromApi();
                        if (!string.IsNullOrEmpty(q)) { ssid = q; isOpen = o; break; }
                    }

                    if (!string.IsNullOrEmpty(ssid))
                        FireIfChanged(ssid, isOpen);
                }
            }
            catch { }
        }

        /// <summary>
        /// Only fires SsidChanged when the SSID actually changes.
        /// Prevents duplicate events from ACM_CONNECTED + ACM_CONNECTION_COMPLETE
        /// or multiple rapid notifications for the same network.
        /// </summary>
        private void FireIfChanged(string? ssid, bool isOpen)
        {
            // Normalise null and empty to null
            if (string.IsNullOrEmpty(ssid)) ssid = null;

            // Same SSID as last fired — swallow the duplicate
            if (ssid == _lastFiredSsid) return;

            _lastFiredSsid = ssid;
            CurrentSsid    = ssid;
            IsOpenNetwork  = isOpen;
            SsidChanged?.Invoke(ssid, isOpen);
        }

        // ── Public query ──────────────────────────────────────────────────────
        public (string? ssid, bool isOpen) QueryCurrentSsid()
        {
            if (_handle == IntPtr.Zero) return (null, false);
            var r = ReadCurrentSsidFromApi();
            if (!string.IsNullOrEmpty(r.ssid))
            {
                CurrentSsid   = r.ssid;
                IsOpenNetwork = r.isOpen;
            }
            return r;
        }

        // ── Core WlanQueryInterface reader ────────────────────────────────────
        private (string? ssid, bool isOpen) ReadCurrentSsidFromApi()
        {
            if (_handle == IntPtr.Zero) return (null, false);

            if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var ifList) != 0)
                return (null, false);

            try
            {
                int count = Marshal.ReadInt32(ifList, 0);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = ifList + 8 + i * 532;
                    var    guid  = Marshal.PtrToStructure<Guid>(entry);

                    if (WlanQueryInterface(_handle, ref guid,
                        WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                        IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0)
                        continue;

                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES
                        // +0   isState (1 = connected)
                        // +520 uSSIDLength
                        // +524 ucSSID[32]
                        // +576 bSecurityEnabled
                        int isState = Marshal.ReadInt32(data, 0);
                        if (isState != 1) { WlanFreeMemory(data); continue; }

                        int    len = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
                        byte[] buf = new byte[len];
                        if (len > 0) Marshal.Copy(data + 524, buf, 0, len);
                        string ssid   = Encoding.UTF8.GetString(buf).TrimEnd('\0');
                        bool   isOpen = Marshal.ReadInt32(data, 576) == 0;
                        WlanFreeMemory(data);

                        if (!string.IsNullOrEmpty(ssid))
                        {
                            // Remember the connected adapter's GUID so DNS automation can target
                            // this exact interface (see DnsService). Stable across SSID changes
                            // on the same radio.
                            CurrentInterfaceGuid = guid;
                            return (ssid, isOpen);
                        }
                    }
                    catch { WlanFreeMemory(data); }
                }
            }
            finally { WlanFreeMemory(ifList); }

            return (null, false);
        }

        // ── IDisposable ───────────────────────────────────────────────────────
        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                try { WlanCloseHandle(_handle, IntPtr.Zero); } catch { }
                _handle = IntPtr.Zero;
            }
        }
    }
}
