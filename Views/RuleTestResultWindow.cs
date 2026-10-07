using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// The result of "Test rule" as a themed pop-over, so it is visible even when the activity log is hidden: whether the requirement
    /// is met right now, a sentence why, the line-by-line checks (tick or cross), what the rule would do, and notes (rule disabled,
    /// manual mode, ...). Read-only; the lines come from <see cref="RuleTester"/> and stay English like the log text.
    /// Code-only, same look as the other small dialogs.
    /// </summary>
    internal sealed class RuleTestResultWindow : Window
    {
        public RuleTestResultWindow(Window owner, string ruleName, RuleTestResult r)
        {
            Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Width = 520;
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 60;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;

            var border = new Border
            {
                Background = Res("WindowBg"), BorderBrush = Res("Accent"), BorderThickness = new Thickness(1),
                CornerRadius = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding = new Thickness(20),
            };
            var panel = new StackPanel();

            TextBlock Text(string t, double size, Brush fg, bool bold = false, Thickness? m = null) => new()
            {
                Text = t, FontFamily = ff, FontSize = size, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Margin = m ?? new Thickness(0),
            };

            var title = Text($"{Lang.T("RuleTestTitle")}: {ruleName}", 13, Res("Accent"), true, new Thickness(0, 0, 0, 12));
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            // verdict
            var verdictBrush = Res(r.Met ? "Success" : "Danger");
            var verdict = new Border
            {
                BorderBrush = verdictBrush, BorderThickness = new Thickness(3, 0, 0, 0), Background = Res("CardBg"),
                Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 14),
            };
            var vs = new StackPanel();
            vs.Children.Add(Text((r.Met ? "✔  " : "✘  ") + Lang.T(r.Met ? "RuleTestMet" : "RuleTestNotMet"), 14, verdictBrush, true));
            var summary = char.ToUpperInvariant(r.Summary.Length > 0 ? r.Summary[0] : ' ') + (r.Summary.Length > 1 ? r.Summary[1..] : "");
            vs.Children.Add(Text(summary.TrimEnd('.') + ".", 11, Res("TextPrimary"), false, new Thickness(0, 4, 0, 0)));
            verdict.Child = vs;
            panel.Children.Add(verdict);

            var body = new StackPanel();

            if (r.Checks is { Count: > 0 })
            {
                body.Children.Add(Text(Lang.T("RuleTestChecks"), 10, Res("TextMuted"), true, new Thickness(0, 0, 0, 4)));
                foreach (var c in r.Checks)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                    var mark = Text(c.Ok == true ? "✔" : c.Ok == false ? "✘" : "•", 12,
                                    c.Ok == true ? Res("Success") : c.Ok == false ? Res("Danger") : Res("TextMuted"), true, new Thickness(0, 0, 8, 0));
                    mark.Width = 16;
                    DockPanel.SetDock(mark, Dock.Left);
                    row.Children.Add(mark);
                    row.Children.Add(Text(c.Text, 11, Res("TextPrimary")));
                    body.Children.Add(row);
                }
            }

            if (r.Actions is { Count: > 0 })
            {
                body.Children.Add(Text(Lang.T(r.Met ? "RuleTestActions" : "RuleTestActionsIf"), 10, Res("TextMuted"), true, new Thickness(0, 12, 0, 4)));
                foreach (var a in r.Actions)
                    body.Children.Add(Text("→  " + a, 11, Res(r.Met ? "TextPrimary" : "TextMuted"), false, new Thickness(0, 0, 0, 2)));
            }

            if (r.Notes.Count > 0)
            {
                body.Children.Add(Text(Lang.T("RuleTestNotes"), 10, Res("TextMuted"), true, new Thickness(0, 12, 0, 4)));
                foreach (var n in r.Notes)
                    body.Children.Add(Text("⚠  " + n, 11, Res("WarningColor"), false, new Thickness(0, 0, 0, 2)));
            }

            body.Children.Add(Text(Lang.T("RuleTestReadOnly"), 9, Res("TextMuted"), false, new Thickness(0, 14, 0, 0)));
            panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420, Content = body });

            var close = new Button
            {
                Content = Lang.T("BtnClose"), Style = Application.Current.Resources["PrimaryBtn"] as Style,
                Padding = new Thickness(18, 6, 18, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
                IsDefault = true,
            };
            close.Click += (_, _) => Close();
            panel.Children.Add(close);

            border.Child = panel;
            Content = border;
            Loaded += (_, _) => close.Focus();
        }
    }
}
