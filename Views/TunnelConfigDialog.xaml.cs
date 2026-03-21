using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace WGClientWifiSwitcher.Views
{
    public partial class TunnelConfigDialog : Window
    {
        public string? ResultName   { get; private set; }
        public string? ResultConfig { get; private set; }

        private readonly string? _originalName;

        public TunnelConfigDialog(string? existingName = null, string? existingConfig = null)
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
            // WireGuard uses Curve25519 — generate a random 32-byte scalar,
            // clamp it per RFC 7748, then base64-encode
            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            key[0]  &= 248;
            key[31] &= 127;
            key[31] |= 64;
            PrivateKeyBox.Text = Convert.ToBase64String(key);
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
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
