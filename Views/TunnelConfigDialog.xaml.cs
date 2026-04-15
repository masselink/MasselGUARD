using System;
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

        private readonly string? _originalName;

        public TunnelConfigDialog(string? existingName = null, string? existingConfig = null,
                                  string? existingGroup = null,
                                  string? preConnect = null, string? postConnect = null,
                                  string? preDisconnect = null, string? postDisconnect = null)
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

            // Populate group picker from AppConfig
            GroupPicker.Items.Clear();
            GroupPicker.Items.Add("");
            var groups = MainWindow.GetConfigStatic()?.TunnelGroups;
            if (groups != null)
                foreach (var g in groups) GroupPicker.Items.Add(g.Name);
            GroupPicker.SelectedItem = existingGroup ?? "";

            // Load script fields
            LoadScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed,     preConnect);
            LoadScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed,    postConnect);
            LoadScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed,  preDisconnect);
            LoadScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed, postDisconnect);
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
        private void TabRaw_GotFocus(object sender, RoutedEventArgs e)
        {
            RawBox.Text = BuildConfigFromFields();
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
            // If on raw tab, parse raw back to fields first for validation
            if (TabRaw.IsSelected)
                ParseConfigToFields(RawBox.Text);

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
            ResultConfig = TabRaw.IsSelected ? RawBox.Text : BuildConfigFromFields();
            ResultGroup  = GroupPicker.SelectedItem as string ?? "";
            ResultPreConnectScript     = CaptureScript(PreConnectBox,     PreConnectEmbedBox,     PreConnectEmbed);
            ResultPostConnectScript    = CaptureScript(PostConnectBox,    PostConnectEmbedBox,    PostConnectEmbed);
            ResultPreDisconnectScript  = CaptureScript(PreDisconnectBox,  PreDisconnectEmbedBox,  PreDisconnectEmbed);
            ResultPostDisconnectScript = CaptureScript(PostDisconnectBox, PostDisconnectEmbedBox, PostDisconnectEmbed);
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
