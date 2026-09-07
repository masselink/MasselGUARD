using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MasselGUARD.Views
{
    /// <summary>Structured data for a toast notification.</summary>
    public sealed class ToastNotification
    {
        /// <summary>Category line in the header — e.g. "WiFi Rule Matched".</summary>
        public string Category    { get; init; } = "";

        /// <summary>Primary line — rule name / tunnel name.</summary>
        public string Primary     { get; init; } = "";

        /// <summary>Secondary line — e.g. "MasselNET → disconnect". Optional.</summary>
        public string? Secondary  { get; init; }

        /// <summary>Strip colour. Null = Accent.</summary>
        public string? StripColor { get; init; }

        public int DurationMs { get; init; } = 5000;

        // ── Interactive mode (Confirm / Cancel buttons) ───────────────────────
        /// <summary>When true, the toast shows Confirm/Cancel buttons and does not auto-dismiss
        /// (a long safety timeout still fires <see cref="OnCancel"/> so it can't linger forever).</summary>
        public bool     Interactive  { get; init; } = false;
        public string   ConfirmLabel { get; init; } = "OK";
        public string   CancelLabel  { get; init; } = "Cancel";
        public Action?  OnConfirm    { get; init; }
        /// <summary>Fired once if the toast is dismissed any way other than Confirm (button, ✕, or timeout).</summary>
        public Action?  OnCancel     { get; init; }
    }

    /// <summary>
    /// Themed WPF toast notification (Option C):
    ///   Header:    🛡  {AppName}  ·  {Category}          [✕]
    ///   Primary:   {Primary}
    ///   Secondary: {Secondary}   (optional, muted)
    /// Slides in from bottom-right, auto-closes after DurationMs.
    /// </summary>
    internal sealed class ToastWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly ToastNotification _n;
        private bool _closing = false;
        private bool _answered = false;

        /// <summary>True while an interactive toast is still waiting for Confirm/Cancel —
        /// used so routine toasts don't dismiss a decision the user hasn't made yet.</summary>
        public bool AwaitingDecision => _n.Interactive && !_answered;

        public ToastWindow(ToastNotification n)
        {
            _n = n;
            WindowStyle        = WindowStyle.None;
            AllowsTransparency = true;
            Background         = Brushes.Transparent;
            ResizeMode         = ResizeMode.NoResize;
            ShowInTaskbar      = false;
            Topmost            = true;
            SizeToContent      = SizeToContent.Height;
            Width              = 360;
            Focusable          = false;

            // ── Resolve theme resources ───────────────────────────────────────
            Brush Res(string key, Color fb) =>
                Application.Current?.Resources[key] as Brush
                ?? new SolidColorBrush(fb);

            CornerRadius corner =
                Application.Current?.Resources["Theme.CornerRadius"] is CornerRadius cr
                    ? cr : new CornerRadius(6);

            FontFamily font =
                Application.Current?.Resources["Theme.FontFamily"] as FontFamily
                ?? new FontFamily("Segoe UI");

            string appName =
                Application.Current?.Resources["Theme.AppName"] as string
                ?? "MasselGUARD";

            var cardBg    = Res("CardBg",      Color.FromRgb(28, 33, 40));
            var borderBr  = Res("BorderColor", Color.FromRgb(33, 38, 45));
            var accentBr  = Res("Accent",      Color.FromRgb(88, 166, 255));
            var textPri   = Res("TextPrimary", Color.FromRgb(230, 237, 243));
            var textMuted = Res("TextMuted",   Color.FromRgb(139, 148, 158));
            var surface   = Res("Surface",     Color.FromRgb(22, 27, 34));

            // Strip colour — resource key or hex, fallback to Accent
            Brush stripBr = accentBr;
            if (!string.IsNullOrEmpty(n.StripColor))
            {
                // Try as a resource key first (e.g. "Success", "Warning", "Accent")
                var resVal = Application.Current?.Resources[n.StripColor];
                if (resVal is Brush resBrush)
                    stripBr = resBrush;
                else if (resVal is Color resColor)
                    stripBr = new SolidColorBrush(resColor);
                else
                {
                    // Try as a hex colour string
                    try
                    {
                        var c = (Color)ColorConverter.ConvertFromString(n.StripColor);
                        stripBr = new SolidColorBrush(c);
                    }
                    catch { }
                }
            }

            // ── Shield icon ───────────────────────────────────────────────────
            UIElement? shieldElem = BuildShieldImage(stripBr);

            // ── Root ──────────────────────────────────────────────────────────
            var outer = new Border
            {
                CornerRadius    = corner,
                Background      = cardBg,
                BorderBrush     = borderBr,
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black, Opacity = 0.6,
                    BlurRadius = 20, ShadowDepth = 4, Direction = 270,
                },
            };

            var rootGrid = new Grid();
            // Left colour strip
            var strip = new Border
            {
                Width           = 4,
                Background      = stripBr,
                CornerRadius    = new CornerRadius(corner.TopLeft, 0, 0, corner.BottomLeft),
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var content = new StackPanel
            {
                Margin = new Thickness(4 + 12, 0, 0, 0), // 4px strip + 12px padding
            };

            // ── Header row ────────────────────────────────────────────────────
            var headerRow = new Grid { Margin = new Thickness(0, 10, 10, 6) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (shieldElem != null)
            {
                shieldElem.SetValue(Grid.ColumnProperty, 0);
                ((FrameworkElement)shieldElem).Margin = new Thickness(0, 0, 6, 0);
                ((FrameworkElement)shieldElem).VerticalAlignment = VerticalAlignment.Center;
                headerRow.Children.Add(shieldElem);
            }

            var appNameBlock = new TextBlock
            {
                Text              = appName,
                FontFamily        = font,
                FontSize          = 11,
                FontWeight        = FontWeights.Bold,
                Foreground        = textPri,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(appNameBlock, 1);
            headerRow.Children.Add(appNameBlock);

            var separator = new TextBlock
            {
                Text              = "  ·  ",
                FontFamily        = font,
                FontSize          = 11,
                Foreground        = textMuted,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(separator, 2);
            headerRow.Children.Add(separator);

            var catBlock = new TextBlock
            {
                Text              = n.Category,
                FontFamily        = font,
                FontSize          = 11,
                FontWeight        = FontWeights.SemiBold,
                Foreground        = stripBr,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(catBlock, 3);
            headerRow.Children.Add(catBlock);

            var closeBtn = new Button
            {
                Content             = "✕",
                FontFamily          = font,
                FontSize            = 10,
                Foreground          = textMuted,
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                Padding             = new Thickness(6, 0, 2, 0),
                Cursor              = System.Windows.Input.Cursors.Hand,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeBtn.MouseEnter += (_, _) => closeBtn.Foreground = textPri;
            closeBtn.MouseLeave += (_, _) => closeBtn.Foreground = textMuted;
            closeBtn.Click      += (_, _) => { if (n.Interactive) Cancel(); else DismissNow(); };
            Grid.SetColumn(closeBtn, 4);
            headerRow.Children.Add(closeBtn);

            content.Children.Add(headerRow);

            // Thin separator line below header
            content.Children.Add(new Border
            {
                Height          = 1,
                Background      = borderBr,
                Margin          = new Thickness(0, 0, 12, 8),
            });

            // ── Primary line ──────────────────────────────────────────────────
            content.Children.Add(new TextBlock
            {
                Text         = n.Primary,
                FontFamily   = font,
                FontSize     = 13,
                FontWeight   = FontWeights.SemiBold,
                Foreground   = textPri,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 12, string.IsNullOrEmpty(n.Secondary) ? 12 : 4),
            });

            // ── Secondary line (optional) ─────────────────────────────────────
            if (!string.IsNullOrEmpty(n.Secondary))
            {
                content.Children.Add(new TextBlock
                {
                    Text         = n.Secondary,
                    FontFamily   = font,
                    FontSize     = 11,
                    Foreground   = textMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 0, 12, 12),
                });
            }

            // ── Interactive buttons (Confirm / Cancel) ────────────────────────
            if (n.Interactive)
            {
                var btnRow = new StackPanel
                {
                    Orientation         = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin              = new Thickness(0, 2, 12, 12),
                };
                Button Btn(string text, string styleKey) => new()
                {
                    Content  = text,
                    Style    = Application.Current?.Resources[styleKey] as Style,
                    FontSize = 11,
                    Padding  = new Thickness(14, 5, 14, 5),
                    Margin   = new Thickness(8, 0, 0, 0),
                };
                var cancelBtn  = Btn(n.CancelLabel,  "FlatBtn");
                var confirmBtn = Btn(n.ConfirmLabel, "PrimaryBtn");
                cancelBtn.Click  += (_, _) => Cancel();
                confirmBtn.Click += (_, _) => Confirm();
                btnRow.Children.Add(cancelBtn);
                btnRow.Children.Add(confirmBtn);
                content.Children.Add(btnRow);
            }

            rootGrid.Children.Add(content);
            rootGrid.Children.Add(strip);
            outer.Child = rootGrid;
            Content = outer;

            // A body click dismisses a normal toast; interactive ones must be answered.
            MouseLeftButtonDown += (_, _) => { if (!n.Interactive) DismissNow(); };

            // Interactive toasts don't auto-dismiss on the normal duration — a long
            // safety timeout counts as Cancel so they can't linger forever.
            Loaded += (_, _) => { PositionWindow(); SlideIn(); };

            int dur = n.Interactive ? 120_000 : n.DurationMs;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dur) };
            _timer.Tick += (_, _) => { if (n.Interactive) Cancel(); else DismissNow(); };
            _timer.Start();

            // Any close that wasn't an explicit Confirm counts as Cancel.
            Closed += (_, _) =>
            {
                if (_n.Interactive && !_answered)
                {
                    _answered = true;
                    try { _n.OnCancel?.Invoke(); } catch { }
                }
            };
        }

        private void Confirm()
        {
            if (_answered) return;
            _answered = true;
            try { _n.OnConfirm?.Invoke(); } catch { }
            DismissNow();
        }

        private void Cancel()
        {
            if (_answered) return;
            _answered = true;
            try { _n.OnCancel?.Invoke(); } catch { }
            DismissNow();
        }

        // ── Shield icon ───────────────────────────────────────────────────────
        private static UIElement? BuildShieldImage(Brush fill)
        {
            try
            {
                const int S = 18;
                var bmp = new System.Drawing.Bitmap(S, S,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);

                var col = System.Drawing.Color.FromArgb(88, 166, 255);
                if (fill is SolidColorBrush scb)
                    col = System.Drawing.Color.FromArgb(
                        scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);

                float X(float x) => x * S / 16f;
                float Y(float y) => y * S / 16f;
                var path = new System.Drawing.Drawing2D.GraphicsPath();
                path.AddLine(X(8), Y(1), X(14), Y(3.5f));
                path.AddLine(X(14), Y(3.5f), X(14), Y(9));
                path.AddBezier(X(14), Y(9), X(14), Y(13), X(11), Y(15), X(8), Y(16));
                path.AddBezier(X(8), Y(16), X(5), Y(15), X(2), Y(13), X(2), Y(9));
                path.AddLine(X(2), Y(9), X(2), Y(3.5f));
                path.CloseFigure();
                using (var b = new System.Drawing.SolidBrush(col)) g.FillPath(b, path);
                path.Dispose();

                var hbmp = bmp.GetHbitmap();
                try
                {
                    var src = System.Windows.Interop.Imaging
                        .CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                    return new Image { Source = src, Width = 18, Height = 18 };
                }
                finally { DeleteObject(hbmp); }
            }
            catch { return null; }
        }

        // ── Positioning ───────────────────────────────────────────────────────
        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 16;
            Top  = area.Bottom - ActualHeight - 16;
        }

        // ── Animations ────────────────────────────────────────────────────────
        private void SlideIn()
        {
            var area = SystemParameters.WorkArea;
            double from = area.Bottom;
            double to   = area.Bottom - ActualHeight - 16;
            Top = from;
            BeginAnimation(TopProperty, new DoubleAnimation(from, to,
                new Duration(TimeSpan.FromMilliseconds(220)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        private void DismissNow()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            var area = SystemParameters.WorkArea;
            var anim = new DoubleAnimation(Top, area.Bottom + 10,
                new Duration(TimeSpan.FromMilliseconds(180)))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            anim.Completed += (_, _) => { try { Close(); } catch { } };
            BeginAnimation(TopProperty, anim);
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
