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
        public int    ResultMonthlyCapMB       { get; private set; }

        public TunnelMetadataDialog(string tunnelName, string currentGroup,
                                    string currentNotes, List<string> groups,
                                    string preConnect = "", string postConnect = "",
                                    string preDisconnect = "", string postDisconnect = "",
                                    bool isDefault = false, bool isOpenProtection = false,
                                    bool isKillSwitch = false, bool isGlobalAlways = false,
                                    bool isAutoReconnect = false, string autoReconnectMode = "off",
                                    int existingMonthlyCapMB = 0)
        {
            InitializeComponent();

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
                        KillSwitchToggleLabel.Text = "🔒 Kill switch  (controlled globally)";
                }
            }

            // Monthly data cap
            if (MonthlyCapBox != null)
                MonthlyCapBox.Text = existingMonthlyCapMB.ToString();

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
                                AutoReconnectToggleLabel.Text = "🔄 Auto-reconnect  (controlled globally)";
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
            ResultMonthlyCapMB        = int.TryParse(MonthlyCapBox?.Text?.Trim(), out var capv) && capv > 0 ? capv : 0;
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

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
