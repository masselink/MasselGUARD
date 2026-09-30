using System.Windows;
using System.Windows.Controls;

namespace MasselGUARD.Views
{
    /// <summary>
    /// One day / week / month row of a tunnel editor's DATA-USAGE section. "Use history" (default
    /// on) greys the MB box, shows the tunnel's typical usage for the period there and disables Kill
    /// at cap - the usage bar/ring then measures against history, which is informational only. The
    /// user's own cap is kept aside meanwhile; unticking restores it, or pre-fills the box with the
    /// historical value when no cap was set yet (a quick way to set a cap based on history).
    /// Shared by <see cref="TunnelConfigDialog"/> and <see cref="TunnelMetadataDialog"/>.
    /// </summary>
    internal sealed class CapPeriodRow
    {
        private readonly CheckBox _useHistory;
        private readonly TextBox  _box;
        private readonly CheckBox _kill;
        private readonly int?     _historyMB;
        private int               _userCapMB;

        public CapPeriodRow(CheckBox useHistory, TextBox box, CheckBox kill,
                            int userCapMB, bool usesHistory, int? historyMB)
        {
            _useHistory = useHistory;
            _box        = box;
            _kill       = kill;
            _historyMB  = historyMB;
            _userCapMB  = userCapMB;

            _box.Text = userCapMB.ToString();
            _useHistory.IsChecked = usesHistory;
            Apply();
            _useHistory.Checked   += (_, _) => { _userCapMB = Parse(_box.Text, _userCapMB); Apply(); };
            _useHistory.Unchecked += (_, _) => Apply();
        }

        /// <summary>"Use history" is ticked.</summary>
        public bool UsesHistory => _useHistory.IsChecked == true;

        /// <summary>The cap to store: the typed value, or - while history is on - the user's cap kept aside.</summary>
        public int CapMB => UsesHistory ? _userCapMB : Parse(_box.Text, 0);

        private void Apply()
        {
            if (UsesHistory)
            {
                _box.Text      = _historyMB?.ToString() ?? "-";
                _box.IsEnabled = false;
                _box.ToolTip   = Lang.T(_historyMB != null ? "TunnelDialogHistoryBoxTip" : "TunnelDialogNoHistoryTip");
                _kill.IsEnabled = false;
            }
            else
            {
                int start = _userCapMB > 0 ? _userCapMB : _historyMB ?? 0;
                _box.Text      = start.ToString();
                _box.IsEnabled = true;
                _box.ToolTip   = null;
                _kill.IsEnabled = true;
            }
        }

        private static int Parse(string? text, int fallback) =>
            int.TryParse(text?.Trim(), out var v) && v >= 0 ? v : fallback;
    }
}
