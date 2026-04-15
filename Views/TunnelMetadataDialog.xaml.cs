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

        public TunnelMetadataDialog(string tunnelName, string currentGroup,
                                    string currentNotes, List<string> groups,
                                    string preConnect = "", string postConnect = "",
                                    string preDisconnect = "", string postDisconnect = "")
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
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            ResultGroup               = GroupPicker.SelectedItem as string ?? "";
            ResultNotes               = NotesBox.Text.Trim();
            ResultPreConnectScript    = PreConnectBox.Text.Trim();
            ResultPostConnectScript   = PostConnectBox.Text.Trim();
            ResultPreDisconnectScript  = PreDisconnectBox.Text.Trim();
            ResultPostDisconnectScript = PostDisconnectBox.Text.Trim();
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
