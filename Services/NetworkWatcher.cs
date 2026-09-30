using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Turns the bursty Windows network-change events (cable plug/unplug, DHCP renew, dock, adapter
    /// up/down, our own tunnel adapters coming and going) into ONE debounced "network settled" signal
    /// (docs/NetworkIdentity-Design.md section 6). It carries no data: the subscriber takes a fresh
    /// <see cref="MasselGUARD.Models.NetworkSnapshot"/> and compares it with the last one, so events
    /// that changed nothing decision-relevant cost nothing. WPF-free.
    /// </summary>
    public sealed class NetworkWatcher : IDisposable
    {
        private readonly Func<int> _settleMs;
        private readonly object    _lock = new();
        private Timer? _timer;
        private bool   _started, _disposed;

        /// <summary>Raised on a thread-pool thread once the burst of events has settled.</summary>
        public event Action? Settled;

        /// <param name="settleMs">Reads the debounce window each time, so a setting change applies live.</param>
        public NetworkWatcher(Func<int> settleMs) => _settleMs = settleMs;

        public void Start()
        {
            lock (_lock)
            {
                if (_started || _disposed) return;
                _started = true;
            }
            NetworkChange.NetworkAddressChanged      += OnChanged;
            NetworkChange.NetworkAvailabilityChanged += OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e) => Trigger();

        /// <summary>(Re)start the settle timer. <paramref name="delayMs"/> below zero uses the configured
        /// window; 0 fires as soon as possible (callers that already debounced, e.g. the Wi-Fi path).</summary>
        public void Trigger(int delayMs = -1)
        {
            int ms = delayMs >= 0 ? delayMs : Math.Clamp(_settleMs(), 0, 30_000);
            lock (_lock)
            {
                if (_disposed) return;
                _timer?.Dispose();
                _timer = new Timer(_ =>
                {
                    try { Settled?.Invoke(); } catch { /* a subscriber must never kill the timer thread */ }
                }, null, ms, Timeout.Infinite);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
            if (_started)
            {
                NetworkChange.NetworkAddressChanged      -= OnChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnChanged;
            }
        }
    }
}
