using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class TunnelConfigDialog : Window
    {
        public string? ResultName              { get; private set; }
        public string? ResultConfig            { get; private set; }
        public string  ResultGroup             { get; private set; } = "";
        public string  ResultPreConnectScript  { get; private set; } = "";
        public string  ResultPostConnectScript { get; private set; } = "";
        public string  ResultPreDisconnectScript  { get; private set; } = "";
        public string  ResultPostDisconnectScript { get; private set; } = "";
        public bool    ResultIsDefault         { get; private set; }
        public bool    ResultIsOpenProtection  { get; private set; }
        public bool    ResultKillSwitch        { get; private set; }
        public bool    ResultAutoReconnect     { get; private set; }
        public int     ResultDailyCapMB        { get; private set; }
        public int     ResultWeeklyCapMB       { get; private set; }
        public int     ResultMonthlyCapMB      { get; private set; }
        public bool    ResultDailyCapKill      { get; private set; }
        public bool    ResultWeeklyCapKill     { get; private set; }
        public bool    ResultMonthlyCapKill    { get; private set; }

        private readonly string? _originalName;

        public TunnelConfigDialog(string? existingName = null, string? existingConfig = null,
                                  string? existingGroup = null,
                                  string? preConnect = null, string? postConnect = null,
                                  string? preDisconnect = null, string? postDisconnect = null,
                                  bool isDefault = false, bool isOpenProtection = false,
                                  List<string>? groupNames = null,
                                  bool isKillSwitch = false, bool isGlobalAlways = false,
                                  bool isAutoReconnect = false, string autoReconnectMode = "off",
                                  int existingMonthlyCapMB = 0,
                                  int existingDailyCapMB = 0, int existingWeeklyCapMB = 0,
                                  bool existingDailyCapKill = false, bool existingWeeklyCapKill = false,
                                  bool existingMonthlyCapKill = false,
                                  long existingDailyUsedBytes = 0, long existingWeeklyUsedBytes = 0,
                                  long existingMonthlyUsedBytes = 0)
        {
            InitializeComponent();
            _originalName = existingName;

            if (existingName != null)
            {
                DialogTitle.Text = Lang.T("TunnelDialogEditTitle");
                Title            = Lang.T("TunnelDialogEditTitle");
            }
            else
            {
                DialogTitle.Text = Lang.T("TunnelDialogAddTitle");
                Title            = Lang.T("TunnelDialogAddTitle");
            }

            if (!string.IsNullOrEmpty(existingName))
                NameBox.Text = existingName;

            if (!string.IsNullOrEmpty(existingConfig))
                LoadFromConfig(existingConfig);

            // Populate group picker — use passed names or fall back to live config
            GroupPicker.Items.Clear();
            GroupPicker.Items.Add("");
            var groups = groupNames
                ?? MainWindow.GetConfigStatic()?.TunnelGroups?.Select(g => g.Name).ToList()
                ?? new List<string>();
            foreach (var g in groups) GroupPicker.Items.Add(g);
            GroupPicker.SelectedItem = existingGroup ?? "";

            // Load script fields
            LoadScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed,     preConnect);
            LoadScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed,    postConnect);
            LoadScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed,  preDisconnect);
            LoadScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed, postDisconnect);

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

            // Data-usage warning thresholds + "show in row" flags
            if (DailyCapBox   != null) DailyCapBox.Text   = existingDailyCapMB.ToString();
            if (WeeklyCapBox  != null) WeeklyCapBox.Text  = existingWeeklyCapMB.ToString();
            if (MonthlyCapBox != null) MonthlyCapBox.Text = existingMonthlyCapMB.ToString();
            if (DailyCapKillChk   != null) DailyCapKillChk.IsChecked   = existingDailyCapKill;
            if (WeeklyCapKillChk  != null) WeeklyCapKillChk.IsChecked  = existingWeeklyCapKill;
            if (MonthlyCapKillChk != null) MonthlyCapKillChk.IsChecked = existingMonthlyCapKill;
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
                                AutoReconnectToggleLabel.Text = "🔄 Auto-reconnect  (controlled globally)";
                        }
                    }
                }
            }
        }

        // ── Load config into form fields ─────────────────────────────────────
        public void LoadFromConfig(string configText)
        {
            RawBox.Text = configText;
            ParseConfigToFields(configText);
        }

        private void ParseConfigToFields(string text)
        {
            string section = "";
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].ToLowerInvariant();
                    continue;
                }
                if (line.StartsWith('#') || !line.Contains('=')) continue;
                var idx = line.IndexOf('=');
                var key = line[..idx].Trim().ToLowerInvariant();
                var val = line[(idx + 1)..].Trim();

                if (section == "interface")
                    switch (key)
                    {
                        case "privatekey":   PrivateKeyBox.Text  = val; break;
                        case "address":      AddressBox.Text     = val; break;
                        case "dns":          DnsBox.Text         = val; break;
                        case "listenport":   ListenPortBox.Text  = val; break;
                        case "mtu":          MtuBox.Text         = val; break;
                    }
                else if (section == "peer")
                    switch (key)
                    {
                        case "publickey":        PublicKeyBox.Text    = val; break;
                        case "presharedkey":     PresharedKeyBox.Text = val; break;
                        case "endpoint":         EndpointBox.Text     = val; break;
                        case "allowedips":       AllowedIPsBox.Text   = val; break;
                        case "persistentkeepalive": KeepaliveBox.Text = val; break;
                    }
            }
        }

        // ── Build config from form fields ─────────────────────────────────────
        private string BuildConfigFromFields()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Interface]");
            if (!string.IsNullOrWhiteSpace(PrivateKeyBox.Text))
                sb.AppendLine($"PrivateKey = {PrivateKeyBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(AddressBox.Text))
                sb.AppendLine($"Address = {AddressBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(DnsBox.Text))
                sb.AppendLine($"DNS = {DnsBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(ListenPortBox.Text))
                sb.AppendLine($"ListenPort = {ListenPortBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(MtuBox.Text))
                sb.AppendLine($"MTU = {MtuBox.Text.Trim()}");

            sb.AppendLine();
            sb.AppendLine("[Peer]");
            if (!string.IsNullOrWhiteSpace(PublicKeyBox.Text))
                sb.AppendLine($"PublicKey = {PublicKeyBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(PresharedKeyBox.Text))
                sb.AppendLine($"PresharedKey = {PresharedKeyBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(EndpointBox.Text))
                sb.AppendLine($"Endpoint = {EndpointBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(AllowedIPsBox.Text))
                sb.AppendLine($"AllowedIPs = {AllowedIPsBox.Text.Trim()}");
            if (!string.IsNullOrWhiteSpace(KeepaliveBox.Text))
                sb.AppendLine($"PersistentKeepalive = {KeepaliveBox.Text.Trim()}");

            return sb.ToString();
        }

        // ── When switching to raw tab — sync fields → raw ─────────────────────
        // Raw shows the WireGuard config AND the MasselGUARD settings as readable
        // "# MasselGUARD-<Key>:" comment lines, so Raw is a full editable view.
        private void TabRaw_GotFocus(object sender, RoutedEventArgs e)
        {
            RawBox.Text = Services.TunnelExportService.BuildExportText(
                BuildConfigFromFields(), DialogSettings());
        }

        /// <summary>Snapshot the MasselGUARD settings the dialog manages, for the Raw view / round-trip.</summary>
        private Services.TunnelExportService.TunnelSettings DialogSettings()
        {
            bool? auto = AutoReconnectRow?.Visibility == Visibility.Visible
                ? AutoReconnectToggle?.IsChecked == true
                : (bool?)null;
            return new Services.TunnelExportService.TunnelSettings
            {
                Group                = GroupPicker.SelectedItem as string,
                KillSwitch           = KillSwitchToggle?.IsChecked == true,
                AutoReconnect        = auto,
                DailyCapMB           = CapMB(DailyCapBox),
                WeeklyCapMB          = CapMB(WeeklyCapBox),
                MonthlyCapMB         = CapMB(MonthlyCapBox),
                PreConnectScript     = CaptureScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed),
                PostConnectScript    = CaptureScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed),
                PreDisconnectScript  = CaptureScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed),
                PostDisconnectScript = CaptureScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed),
            };
        }

        /// <summary>
        /// Apply settings parsed from the Raw tab back onto the form controls, so
        /// editing them in Raw round-trips. A setting absent from the Raw text is
        /// left unchanged (absent ≠ "clear"). Globally-controlled toggles are not
        /// overridden.
        /// </summary>
        private void ApplySettingsToControls(Services.TunnelExportService.TunnelSettings s)
        {
            if (s.Group != null)
            {
                if (!GroupPicker.Items.Contains(s.Group)) GroupPicker.Items.Add(s.Group);
                GroupPicker.SelectedItem = s.Group;
            }
            if (s.KillSwitch.HasValue && KillSwitchToggle is { IsEnabled: true })
                KillSwitchToggle.IsChecked = s.KillSwitch.Value;
            if (s.AutoReconnect.HasValue && AutoReconnectToggle is { IsEnabled: true }
                && AutoReconnectRow?.Visibility == Visibility.Visible)
                AutoReconnectToggle.IsChecked = s.AutoReconnect.Value;
            if (s.DailyCapMB.HasValue   && DailyCapBox   != null) DailyCapBox.Text   = s.DailyCapMB.Value.ToString();
            if (s.WeeklyCapMB.HasValue  && WeeklyCapBox  != null) WeeklyCapBox.Text  = s.WeeklyCapMB.Value.ToString();
            if (s.MonthlyCapMB.HasValue && MonthlyCapBox != null) MonthlyCapBox.Text = s.MonthlyCapMB.Value.ToString();

            if (s.PreConnectScript     != null) ReloadScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed,     s.PreConnectScript);
            if (s.PostConnectScript    != null) ReloadScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed,    s.PostConnectScript);
            if (s.PreDisconnectScript  != null) ReloadScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed,  s.PreDisconnectScript);
            if (s.PostDisconnectScript != null) ReloadScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed, s.PostDisconnectScript);
        }

        /// <summary>Parse a cap TextBox to a non-negative MB value (0 = off / blank / invalid).</summary>
        private static int CapMB(System.Windows.Controls.TextBox? box)
            => int.TryParse(box?.Text?.Trim(), out var v) && v > 0 ? v : 0;

        /// <summary>"· 320 MB used" for the period's current usage, or empty when none.</summary>
        private static string UsedText(long bytes)
            => bytes > 0 ? $"· {ViewModels.MainViewModel.FmtBytes(bytes)} used" : "";

        private static void ReloadScript(
            System.Windows.Controls.TextBox pathBox,
            System.Windows.Controls.TextBox embedBox,
            System.Windows.Controls.Primitives.ToggleButton embedToggle,
            string value)
        {
            pathBox.Text        = "";
            embedBox.Text       = "";
            embedToggle.IsChecked = false;
            embedBox.Visibility = Visibility.Collapsed;
            LoadScript(pathBox, embedBox, embedToggle, value);
        }

        // ── Generate WireGuard private key (Curve25519) ───────────────────────
        private void GenerateKey_Click(object sender, RoutedEventArgs e)
        {
            // Use tunnel.dll if available (generates proper Curve25519 keypair including public key)
            // Falls back to pure C# clamping if tunnel.dll is absent
            var (priv, pub) = TunnelDll.GenerateKeypair();
            PrivateKeyBox.Text = priv;
            if (!string.IsNullOrEmpty(pub))
                PublicKeyBox.Text = pub; // only populated when tunnel.dll provides it
        }

        // ── Validate and save ─────────────────────────────────────────────────
        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            // If on raw tab, split off the MasselGUARD settings, parse the config
            // back to fields (for validation), and apply the settings to the form
            // controls — Raw is authoritative for what it contains.
            string? rawConfig = null;
            if (TabRaw.IsSelected)
            {
                var (cfg, settings) = Services.TunnelExportService.ParseImportText(RawBox.Text);
                rawConfig = cfg;
                ParseConfigToFields(cfg);
                if (settings != null) ApplySettingsToControls(settings);
            }

            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show(Lang.T("TunnelNameRequired"), Lang.T("TunnelValidationTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                NameBox.Focus();
                return;
            }
            // Sanitise name for use as filename
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            if (string.IsNullOrWhiteSpace(PrivateKeyBox.Text) && !TabRaw.IsSelected)
            {
                MessageBox.Show(Lang.T("TunnelPrivateKeyRequired"), Lang.T("TunnelValidationTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                PrivateKeyBox.Focus();
                return;
            }

            ResultName   = name;
            // Store only the clean WireGuard config — the MasselGUARD settings live
            // in the StoredTunnel fields (captured below from the controls).
            ResultConfig = TabRaw.IsSelected ? rawConfig! : BuildConfigFromFields();
            ResultGroup  = GroupPicker.SelectedItem as string ?? "";
            ResultPreConnectScript     = CaptureScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed);
            ResultPostConnectScript    = CaptureScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed);
            ResultPreDisconnectScript  = CaptureScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed);
            ResultPostDisconnectScript = CaptureScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed);
            ResultIsDefault        = IsDefaultToggle?.IsChecked        == true;
            ResultIsOpenProtection = IsOpenProtectionToggle?.IsChecked == true;
            ResultKillSwitch       = KillSwitchToggle?.IsChecked       == true;
            ResultAutoReconnect    = AutoReconnectToggle?.IsChecked    == true;
            ResultDailyCapMB   = CapMB(DailyCapBox);
            ResultWeeklyCapMB  = CapMB(WeeklyCapBox);
            ResultMonthlyCapMB = CapMB(MonthlyCapBox);
            ResultDailyCapKill   = DailyCapKillChk?.IsChecked   == true;
            ResultWeeklyCapKill  = WeeklyCapKillChk?.IsChecked  == true;
            ResultMonthlyCapKill = MonthlyCapKillChk?.IsChecked == true;
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // ── Script helpers ────────────────────────────────────────────────────
        private const string EmbedPrefix = "@embed:";

        private static void LoadScript(
            System.Windows.Controls.TextBox pathBox,
            System.Windows.Controls.TextBox embedBox,
            System.Windows.Controls.Primitives.ToggleButton embedToggle,
            string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.StartsWith(EmbedPrefix, StringComparison.Ordinal))
            {
                embedToggle.IsChecked = true;
                embedBox.Text         = value[EmbedPrefix.Length..];
                embedBox.Visibility   = Visibility.Visible;
            }
            else
            {
                pathBox.Text = value;
            }
        }

        private static string CaptureScript(
            System.Windows.Controls.TextBox pathBox,
            System.Windows.Controls.TextBox embedBox,
            System.Windows.Controls.Primitives.ToggleButton embedToggle)
        {
            if (embedToggle.IsChecked == true)
            {
                var content = embedBox.Text.Trim();
                return string.IsNullOrEmpty(content) ? "" : EmbedPrefix + content;
            }
            return pathBox.Text.Trim();
        }

        private void EmbedToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
            bool embed = tb.IsChecked == true;
            string tag = tb.Tag as string ?? "";

            var embedBox = tag switch
            {
                "PreConnect"     => PreConnectEmbedBox,
                "PostConnect"    => PostConnectEmbedBox,
                "PreDisconnect"  => PreDisconnectEmbedBox,
                "PostDisconnect" => PostDisconnectEmbedBox,
                _ => (System.Windows.Controls.TextBox?)null
            };
            var pathBox = tag switch
            {
                "PreConnect"     => PreConnectBox,
                "PostConnect"    => PostConnectBox,
                "PreDisconnect"  => PreDisconnectBox,
                "PostDisconnect" => PostDisconnectBox,
                _ => (System.Windows.Controls.TextBox?)null
            };
            if (embedBox == null) return;

            if (embed)
            {
                if (!string.IsNullOrEmpty(pathBox?.Text) && File.Exists(pathBox.Text))
                {
                    try { embedBox.Text = File.ReadAllText(pathBox.Text, System.Text.Encoding.UTF8); }
                    catch { }
                }
                embedBox.Visibility = Visibility.Visible;
            }
            else
            {
                embedBox.Visibility = Visibility.Collapsed;
            }
        }

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
