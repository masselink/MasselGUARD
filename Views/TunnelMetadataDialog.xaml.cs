using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class TunnelMetadataDialog : Window
    {
        public string ResultGroup              { get; private set; } = "";
        public string ResultNotes              { get; private set; } = "";
        public string ResultPreConnectScript   { get; private set; } = "";
        public string ResultPostConnectScript  { get; private set; } = "";
        public string ResultPreDisconnectScript  { get; private set; } = "";
        public string ResultPostDisconnectScript { get; private set; } = "";
        public bool   ResultIsDefault          { get; private set; }
        public bool   ResultIsOpenProtection   { get; private set; }
        public bool   ResultKillSwitch         { get; private set; }
        public bool   ResultAutoReconnect      { get; private set; }
        public int    ResultDailyCapMB         { get; private set; }
        public int    ResultWeeklyCapMB        { get; private set; }
        public int    ResultMonthlyCapMB       { get; private set; }
        public bool   ResultDailyCapKill       { get; private set; }
        public bool   ResultWeeklyCapKill      { get; private set; }
        public bool   ResultMonthlyCapKill     { get; private set; }
        public bool   ResultDailyCapHideRing   { get; private set; }
        public bool   ResultWeeklyCapHideRing  { get; private set; }
        public bool   ResultMonthlyCapHideRing { get; private set; }
        public bool   ResultDailyCapUseHistory   { get; private set; } = true;
        public bool   ResultWeeklyCapUseHistory  { get; private set; } = true;
        public bool   ResultMonthlyCapUseHistory { get; private set; } = true;

        // Day / week / month rows: "Use history" greys the cap box (see CapPeriodRow).
        private CapPeriodRow? _dailyRow, _weeklyRow, _monthlyRow;

        public TunnelMetadataDialog(string tunnelName, string currentGroup,
                                    string currentNotes, List<string> groups,
                                    string preConnect = "", string postConnect = "",
                                    string preDisconnect = "", string postDisconnect = "",
                                    bool isDefault = false, bool isOpenProtection = false,
                                    bool isKillSwitch = false, bool isGlobalAlways = false,
                                    bool isAutoReconnect = false, string autoReconnectMode = "off",
                                    int existingMonthlyCapMB = 0,
                                    int existingDailyCapMB = 0, int existingWeeklyCapMB = 0,
                                    bool existingDailyCapKill = false, bool existingWeeklyCapKill = false,
                                    bool existingMonthlyCapKill = false,
                                    bool existingDailyCapHideRing = false, bool existingWeeklyCapHideRing = false,
                                    bool existingMonthlyCapHideRing = false,
                                    long existingDailyUsedBytes = 0, long existingWeeklyUsedBytes = 0,
                                    long existingMonthlyUsedBytes = 0,
                                    bool dailyUsesHistory = true, bool weeklyUsesHistory = true,
                                    bool monthlyUsesHistory = true,
                                    (int day, int week, int month)? historyMB = null)
        {
            InitializeComponent();
            DefaultActionToggleLabel.Text  = "⚡ " + Lang.T("BehaviourDefaultAction");
            OpenProtectionToggleLabel.Text = "🔓 " + Lang.T("BehaviourOpenProtection");
            KillSwitchToggleLabel.Text     = "🔒 " + Lang.T("SettingsKillSwitchModeTitle");
            AutoReconnectToggleLabel.Text  = "🔄 " + Lang.T("SettingsAutoReconnectModeTitle");

            DialogTitle.Text     = Lang.T("TunnelMetadataTitle");
            TunnelNameLabel.Text = tunnelName;
            NotesBox.Text        = currentNotes;

            // Populate group picker
            GroupPicker.Items.Add("");
            foreach (var g in groups) GroupPicker.Items.Add(g);
            GroupPicker.SelectedItem = string.IsNullOrEmpty(currentGroup) ? "" : currentGroup;

            // Load script paths
            PreConnectBox.Text    = preConnect;
            PostConnectBox.Text   = postConnect;
            PreDisconnectBox.Text = preDisconnect;
            PostDisconnectBox.Text = postDisconnect;

            // Default / open protection toggles
            if (IsDefaultToggle        != null) IsDefaultToggle.IsChecked        = isDefault;
            if (IsOpenProtectionToggle != null) IsOpenProtectionToggle.IsChecked = isOpenProtection;

            // Kill switch toggle
            if (KillSwitchToggle != null)
            {
                KillSwitchToggle.IsChecked = isKillSwitch;
                if (isGlobalAlways)
                {
                    KillSwitchToggle.IsEnabled = false;
                    KillSwitchToggle.Opacity   = 0.5;
                    if (KillSwitchToggleLabel != null)
                        KillSwitchToggleLabel.Text = "🔒 " + Lang.T("SettingsKillSwitchModeTitle") + "  (" + Lang.T("ControlledGlobally") + ")";
                }
            }

            // Data-usage warning thresholds + "show in row" flags
            if (DailyCapKillChk   != null) DailyCapKillChk.IsChecked   = existingDailyCapKill;
            if (WeeklyCapKillChk  != null) WeeklyCapKillChk.IsChecked  = existingWeeklyCapKill;
            if (MonthlyCapKillChk != null) MonthlyCapKillChk.IsChecked = existingMonthlyCapKill;
            if (DailyHideRingChk   != null) DailyHideRingChk.IsChecked   = existingDailyCapHideRing;
            if (WeeklyHideRingChk  != null) WeeklyHideRingChk.IsChecked  = existingWeeklyCapHideRing;
            if (MonthlyHideRingChk != null) MonthlyHideRingChk.IsChecked = existingMonthlyCapHideRing;
            _dailyRow   = new CapPeriodRow(DailyUseHistoryChk,   DailyCapBox!,   DailyCapKillChk!,
                                           existingDailyCapMB,   dailyUsesHistory,   historyMB?.day);
            _weeklyRow  = new CapPeriodRow(WeeklyUseHistoryChk,  WeeklyCapBox!,  WeeklyCapKillChk!,
                                           existingWeeklyCapMB,  weeklyUsesHistory,  historyMB?.week);
            _monthlyRow = new CapPeriodRow(MonthlyUseHistoryChk, MonthlyCapBox!, MonthlyCapKillChk!,
                                           existingMonthlyCapMB, monthlyUsesHistory, historyMB?.month);
            if (DailyUsedLabel    != null) DailyUsedLabel.Text    = UsedText(existingDailyUsedBytes);
            if (WeeklyUsedLabel   != null) WeeklyUsedLabel.Text   = UsedText(existingWeeklyUsedBytes);
            if (MonthlyUsedLabel  != null) MonthlyUsedLabel.Text  = UsedText(existingMonthlyUsedBytes);

            // Auto-reconnect toggle
            if (AutoReconnectRow != null)
            {
                if (autoReconnectMode == "off")
                {
                    AutoReconnectRow.Visibility = System.Windows.Visibility.Collapsed;
                }
                else
                {
                    AutoReconnectRow.Visibility = System.Windows.Visibility.Visible;
                    if (AutoReconnectToggle != null)
                    {
                        AutoReconnectToggle.IsChecked = isAutoReconnect;
                        if (autoReconnectMode == "always")
                        {
                            AutoReconnectToggle.IsEnabled = false;
                            AutoReconnectToggle.Opacity   = 0.5;
                            if (AutoReconnectToggleLabel != null)
                                AutoReconnectToggleLabel.Text = "🔄 " + Lang.T("SettingsAutoReconnectModeTitle") + "  (" + Lang.T("ControlledGlobally") + ")";
                        }
                    }
                }
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            ResultGroup               = GroupPicker.SelectedItem as string ?? "";
            ResultNotes               = NotesBox.Text.Trim();
            ResultPreConnectScript    = PreConnectBox.Text.Trim();
            ResultPostConnectScript   = PostConnectBox.Text.Trim();
            ResultPreDisconnectScript  = PreDisconnectBox.Text.Trim();
            ResultPostDisconnectScript = PostDisconnectBox.Text.Trim();
            ResultIsDefault           = IsDefaultToggle?.IsChecked        == true;
            ResultIsOpenProtection    = IsOpenProtectionToggle?.IsChecked == true;
            ResultKillSwitch          = KillSwitchToggle?.IsChecked       == true;
            ResultAutoReconnect       = AutoReconnectToggle?.IsChecked    == true;
            ResultDailyCapMB          = _dailyRow?.CapMB   ?? CapMB(DailyCapBox);
            ResultWeeklyCapMB         = _weeklyRow?.CapMB  ?? CapMB(WeeklyCapBox);
            ResultMonthlyCapMB        = _monthlyRow?.CapMB ?? CapMB(MonthlyCapBox);
            ResultDailyCapUseHistory   = _dailyRow?.UsesHistory   ?? true;
            ResultWeeklyCapUseHistory  = _weeklyRow?.UsesHistory  ?? true;
            ResultMonthlyCapUseHistory = _monthlyRow?.UsesHistory ?? true;
            ResultDailyCapKill        = DailyCapKillChk?.IsChecked   == true;
            ResultWeeklyCapKill       = WeeklyCapKillChk?.IsChecked  == true;
            ResultMonthlyCapKill      = MonthlyCapKillChk?.IsChecked == true;
            ResultDailyCapHideRing    = DailyHideRingChk?.IsChecked   == true;
            ResultWeeklyCapHideRing   = WeeklyHideRingChk?.IsChecked  == true;
            ResultMonthlyCapHideRing  = MonthlyHideRingChk?.IsChecked == true;
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Parse a cap TextBox to a non-negative MB value (0 = off / blank / invalid).</summary>
        private static int CapMB(System.Windows.Controls.TextBox? box)
            => int.TryParse(box?.Text?.Trim(), out var v) && v > 0 ? v : 0;

        /// <summary>"· 320 MB used" for the period's current usage, or empty when none.</summary>
        private static string UsedText(long bytes)
            => bytes > 0 ? $"· {ViewModels.MainViewModel.FmtBytes(bytes)} used" : "";

        private void BrowseScript(System.Windows.Controls.TextBox pathBox)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = Lang.T("TunnelScriptBrowseTitle"),
                Filter = "Scripts (*.bat;*.ps1)|*.bat;*.ps1|Batch files (*.bat)|*.bat|PowerShell (*.ps1)|*.ps1|All files (*.*)|*.*",
            };
            if (!string.IsNullOrEmpty(pathBox.Text) && File.Exists(pathBox.Text))
                dlg.InitialDirectory = Path.GetDirectoryName(pathBox.Text);
            if (dlg.ShowDialog() == true)
                pathBox.Text = dlg.FileName;
        }

        private void BrowsePreConnect_Click(object sender, RoutedEventArgs e)     => BrowseScript(PreConnectBox);
        private void BrowsePostConnect_Click(object sender, RoutedEventArgs e)    => BrowseScript(PostConnectBox);
        private void BrowsePreDisconnect_Click(object sender, RoutedEventArgs e)  => BrowseScript(PreDisconnectBox);
        private void BrowsePostDisconnect_Click(object sender, RoutedEventArgs e) => BrowseScript(PostDisconnectBox);

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
