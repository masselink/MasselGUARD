using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class TunnelMetadataDialog : Window
    {
        public string ResultGroup { get; private set; } = "";
        public string ResultNotes { get; private set; } = "";

        public TunnelMetadataDialog(string tunnelName, string currentGroup,
                                    string currentNotes, List<string> groups)
        {
            InitializeComponent();

            DialogTitle.Text    = Lang.T("TunnelMetadataTitle");
            TunnelNameLabel.Text = tunnelName;
            NotesBox.Text        = currentNotes;

            // Populate group picker
            GroupPicker.Items.Add("");          // (none / ungrouped)
            foreach (var g in groups) GroupPicker.Items.Add(g);
            GroupPicker.SelectedItem = string.IsNullOrEmpty(currentGroup) ? "" : currentGroup;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            ResultGroup  = GroupPicker.SelectedItem as string ?? "";
            ResultNotes  = NotesBox.Text.Trim();
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
