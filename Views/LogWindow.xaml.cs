using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// A roomy, resizable window that mirrors the main-window activity log. It reads the same
    /// <see cref="LogService"/> (shared entries + file), so Clear/Export here affect the one log,
    /// and it stays live by subscribing to <see cref="LogService.EntryAdded"/>.
    /// </summary>
    public partial class LogWindow : Window
    {
        private readonly LogService _log;

        public LogWindow(LogService log, Window? owner)
        {
            InitializeComponent();
            _log = log;
            if (owner != null) Owner = owner;

            Rebuild();
            _log.EntryAdded += OnEntryAdded;
            Closed += (_, _) => _log.EntryAdded -= OnEntryAdded;
        }

        private void OnEntryAdded(LogEntry entry)
        {
            if (Dispatcher.CheckAccess()) Append(entry);
            else Dispatcher.BeginInvoke(new Action(() => Append(entry)));
        }

        private void Rebuild()
        {
            LogDocument.Blocks.Clear();
            foreach (var e in _log.Entries) Append(e);
            CountLabel.Text = _log.Count.ToString();
        }

        /// <summary>Render one entry at the top (newest first), matching the main-window styling.</summary>
        private void Append(LogEntry entry)
        {
            var para = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };

            Brush tsBrush;
            try { tsBrush = new SolidColorBrush((Color)FindResource("Theme.LogTimestampColor")); }
            catch { tsBrush = SafeBrush("TextMuted"); }

            var ts = new Run(entry.Timestamp.ToString("HH:mm:ss") + "  ") { Foreground = tsBrush };

            Brush msgBrush = entry.Level switch
            {
                LogLevel.Ok   => SafeBrush("Accent"),
                LogLevel.Warn => SafeBrush("Danger"),
                LogLevel.Info => SafeBrush("Accent"),
                _             => SafeBrush("TextMuted"),
            };

            string prefix = entry.IsContinuation ? "  ↳ " : "";
            para.Inlines.Add(ts);
            para.Inlines.Add(new Run(prefix + entry.Message) { Foreground = msgBrush });

            if (LogDocument.Blocks.FirstBlock != null)
                LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
            else
                LogDocument.Blocks.Add(para);

            CountLabel.Text = _log.Count.ToString();
        }

        private Brush SafeBrush(string key)
        {
            try { return (Brush)FindResource(key); }
            catch { return Brushes.Gray; }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _log.Clear();
            Rebuild();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Text file (*.txt)|*.txt",
                FileName = $"masselguard-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dlg.ShowDialog(this) == true)
            {
                try { _log.ExportToFile(dlg.FileName); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
        }
    }
}
