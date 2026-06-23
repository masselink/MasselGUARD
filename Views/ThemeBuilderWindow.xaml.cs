using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MasselGUARD.Models;
using Path = System.IO.Path;   // disambiguate from System.Windows.Shapes.Path

namespace MasselGUARD.Views
{
    public partial class ThemeBuilderWindow : Window
    {
        // ── State ─────────────────────────────────────────────────────────────
        private readonly MainWindow   _main;
        private ThemeDefinition       _draft    = new();
        private string                _editingName  = "";   // folder name being edited
        private bool                  _readOnly    = false;  // System + Shared: editor locked
        private bool                  _canDelete   = false;  // Shared + Custom: deletable (not System)
        private bool                  _loading      = false;
        private bool                  _dirty        = false; // unsaved edits exist
        // Debounces live apply so slider drags / hex typing don't restyle per keystroke
        private readonly System.Windows.Threading.DispatcherTimer _applyTimer;

        // ── Dual-variant colour sets ──────────────────────────────────────────
        // The colour boxes always show ONE variant; both sets are kept here and
        // written to the dark/light sections of the unified theme format on save.
        private bool _editingDark = true;
        private readonly Dictionary<string, string> _darkVals  = new();
        private readonly Dictionary<string, string> _lightVals = new();
        private Dictionary<string, string> ActiveVals => _editingDark ? _darkVals : _lightVals;

        // ── Per-variant assets ────────────────────────────────────────────────
        // When enabled, logo + background image + tray icons follow the Dark/Light
        // pill (stored in the variant sections); otherwise one shared set lives at
        // root level.
        private bool   _perVariantAssets = false;
        private string _darkLogo  = "", _lightLogo  = "", _darkBgImg = "", _lightBgImg = "";
        private string _darkTrayC = "", _lightTrayC = "", _darkTrayD = "", _lightTrayD = "";

        // Color-picker controls built in BuildColorRows() — both variants shown side by side.
        private readonly Dictionary<string, TextBox> _lightBoxes    = new();
        private readonly Dictionary<string, TextBox> _darkBoxes     = new();
        private readonly Dictionary<string, Border>  _lightSwatches = new();
        private readonly Dictionary<string, Border>  _darkSwatches  = new();
        // Per-row copy arrows; glyph/colour reflects the global Invert mode.
        private readonly List<Button> _arrows = new();

        // Live tray-menu mock preview (built next to the ColorTray* rows)
        private Border? _trayMockBorder;
        private Border? _trayMockHoverRow;
        private readonly List<TextBlock> _trayMockTexts = new();

        // Undo / redo of editor state. _baseline is the current settled state; an edit
        // settling pushes _baseline onto _undo, a restore swaps via _redo. Restores run
        // under _loading so they don't re-enter the capture path.
        private readonly Stack<ThemeSnapshot> _undo = new();
        private readonly Stack<ThemeSnapshot> _redo = new();
        private ThemeSnapshot? _baseline;

        // Color keys + friendly labels in display order
        private static readonly (string key, string label)[] ColorFields =
        {
            ("ColorWindowBg",    "Window background"),
            ("ColorSurface",     "Surface (title/footer)"),
            ("ColorCard",        "Card / list panel"),
            ("ColorBorder",      "Border / divider"),
            ("ColorAccent",      "Accent"),
            ("ColorTextPrimary", "Text — primary"),
            ("ColorTextMuted",   "Text — muted"),
            ("ColorSuccess",     "Success (green)"),
            ("ColorDanger",      "Danger (red)"),
            ("ColorHighlight",   "Highlight"),
            ("ColorError",       "Error text"),
            ("ColorErrorBg",     "Error background"),
            ("ColorWarning",     "Warning text"),
            ("ColorWarningBg",   "Warning background"),
            ("ColorListHover",   "List row — hover"),
            ("ColorListSelected","List row — selected"),
            ("ColorLogTimestamp","Log timestamp"),
            ("ColorTrayBg",      "Tray menu background"),
            ("ColorTrayHover",   "Tray menu hover"),
            ("ColorTrayText",    "Tray menu text"),
            ("ColorTrayBorder",  "Tray menu border"),
        };

        // ── Constructor ───────────────────────────────────────────────────────
        public ThemeBuilderWindow(MainWindow main)
        {
            _main = main;
            // Pin this window to System (Windows colors): local resources shadow the
            // application resources, so live theme edits restyle the MAIN window but
            // never the builder itself — the editor stays readable even while a
            // broken draft theme is applied.
            ThemeManager.ApplySystemTo(Resources);
            InitializeComponent();
            BuildColorRows();
            PopulateThemeList();

            // Font dropdown — every installed family, rendered in its own typeface.
            // Exclude Windows 11 variable-font *collections* (e.g. "Sans Serif Collection"):
            // they aren't real renderable families and WPF mis-measures them, which blows
            // up control/line heights (e.g. the tunnel list then shows only one row).
            foreach (var ff in Fonts.SystemFontFamilies
                         .Where(f => !IsUnusableFontFamily(f.Source))
                         .OrderBy(f => f.Source))
                FontFamilyBox.Items.Add(ff);

            _applyTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180),
            };
            _applyTimer.Tick += (_, _) => { _applyTimer.Stop(); CaptureUndoStep(); ApplyDraftLive(); };
        }

        /// <summary>Font families WPF cannot use as a normal typeface (Win11 variable-font collections).</summary>
        private static bool IsUnusableFontFamily(string source) =>
            source.Equals("Sans Serif Collection", StringComparison.OrdinalIgnoreCase);

        // ── Live apply ────────────────────────────────────────────────────────

        /// <summary>Marks the draft dirty and schedules a debounced live apply.</summary>
        private void OnEditorChanged()
        {
            if (_loading || _readOnly || string.IsNullOrEmpty(_editingName)) return;
            _dirty = true;
            if (CancelBtn != null) CancelBtn.IsEnabled = true;
            StatusLabel.Text = "";
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        /// <summary>Applies the current draft to the running app (live preview). Runs even
        /// for read-only themes so the Dark/Light pill can preview both variants — edits
        /// are blocked upstream (OnEditorChanged), so this only fires here via the pill.</summary>
        private void ApplyDraftLive()
        {
            if (string.IsNullOrEmpty(_editingName)) return;
            var draft  = CollectDraft();
            var folder = ThemeManager.ThemeFolder(_editingName);
            ThemeManager.Instance.ApplyPreview(draft, folder);
        }

        // ── Undo / redo ───────────────────────────────────────────────────────

        /// <summary>Immutable snapshot of the full editor state (structural + both colour variants + assets).</summary>
        private sealed class ThemeSnapshot
        {
            public ThemeDefinition Def = ThemeDefinition.Default;
            public Dictionary<string, string> Dark  = new();
            public Dictionary<string, string> Light = new();
            public bool EditingDark;
            public bool PerVariant;
            public string DarkLogo = "", LightLogo = "", DarkBgImg = "", LightBgImg = "",
                          DarkTrayC = "", LightTrayC = "", DarkTrayD = "", LightTrayD = "";
        }

        private ThemeSnapshot Snapshot() => new()
        {
            Def         = CollectDraft(),
            Dark        = new Dictionary<string, string>(_darkVals),
            Light       = new Dictionary<string, string>(_lightVals),
            EditingDark = _editingDark,
            PerVariant  = _perVariantAssets,
            DarkLogo    = _darkLogo,  LightLogo  = _lightLogo,
            DarkBgImg   = _darkBgImg, LightBgImg = _lightBgImg,
            DarkTrayC   = _darkTrayC, LightTrayC = _lightTrayC,
            DarkTrayD   = _darkTrayD, LightTrayD = _lightTrayD,
        };

        /// <summary>Records the previous settled state for undo. Called once per debounced edit burst.</summary>
        private void CaptureUndoStep()
        {
            if (_readOnly || string.IsNullOrEmpty(_editingName)) return;
            if (_baseline != null) { _undo.Push(_baseline); _redo.Clear(); }
            _baseline = Snapshot();
            UpdateUndoRedoButtons();
        }

        /// <summary>Repopulates the whole editor from a snapshot and re-applies it live.</summary>
        private void RestoreSnapshot(ThemeSnapshot s)
        {
            _draft = s.Def;
            _darkVals.Clear();  foreach (var kv in s.Dark)  _darkVals[kv.Key]  = kv.Value;
            _lightVals.Clear(); foreach (var kv in s.Light) _lightVals[kv.Key] = kv.Value;
            _editingDark      = s.EditingDark;
            _perVariantAssets = s.PerVariant;
            _darkLogo  = s.DarkLogo;  _lightLogo  = s.LightLogo;
            _darkBgImg = s.DarkBgImg; _lightBgImg = s.LightBgImg;
            _darkTrayC = s.DarkTrayC; _lightTrayC = s.LightTrayC;
            _darkTrayD = s.DarkTrayD; _lightTrayD = s.LightTrayD;

            _applyTimer.Stop();
            _loading = true;
            try { PopulateEditor(); } finally { _loading = false; }

            _dirty = true;
            if (CancelBtn != null) CancelBtn.IsEnabled = true;
            ApplyDraftLive();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undo.Count == 0 || _baseline == null) return;
            _redo.Push(_baseline);
            _baseline = _undo.Pop();
            RestoreSnapshot(_baseline);
            UpdateUndoRedoButtons();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redo.Count == 0 || _baseline == null) return;
            _undo.Push(_baseline);
            _baseline = _redo.Pop();
            RestoreSnapshot(_baseline);
            UpdateUndoRedoButtons();
        }

        private void UpdateUndoRedoButtons()
        {
            if (UndoBtn != null) UndoBtn.IsEnabled = !_readOnly && _undo.Count > 0;
            if (RedoBtn != null) RedoBtn.IsEnabled = !_readOnly && _redo.Count > 0;
        }

        /// <summary>Ctrl+Z / Ctrl+Y, except inside a focused TextBox where they drive the box's own text undo.</summary>
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control;
            bool inTextBox = Keyboard.FocusedElement is TextBox;
            if (ctrl && !inTextBox && !_readOnly)
            {
                if (e.Key == System.Windows.Input.Key.Z) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
                else if (e.Key == System.Windows.Input.Key.Y) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
            }
            base.OnPreviewKeyDown(e);
        }

        // ── Cancel / revert ───────────────────────────────────────────────────

        /// <summary>Discards unsaved edits: reloads the theme from disk and reverts the live app.</summary>
        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnly || string.IsNullOrEmpty(_editingName) || !_dirty) return;
            var name = _editingName;
            LoadTheme(name);                 // reloads from disk → discards edits, resets dirty + undo
            _main.ApplyThemeFromConfig();    // revert the running app to the committed theme
            StatusLabel.Text = "Reverted";
        }

        /// <summary>
        /// When unsaved edits exist, asks whether to save them. Returns after the
        /// choice is handled: save (and activate) or discard (revert the live app
        /// to the committed theme). The themed dialog uses the System palette (not the
        /// live draft), so it stays readable even while a broken draft is applied.
        /// </summary>
        private void ResolveDirtyDraft(string question)
        {
            if (!_dirty || _readOnly || string.IsNullOrEmpty(_editingName)) return;
            if (ThemedMessageDialog.Confirm(this, question, "Theme Builder"))
            {
                TrySaveDraft();
            }
            else
            {
                _dirty = false;
                _main.ApplyThemeFromConfig();   // revert live changes
            }
        }

        // ── Dual-variant editing ──────────────────────────────────────────────

        /// <summary>Harvests the non-empty colour fields of a definition into a value set.</summary>
        private static void FillVariantVals(Dictionary<string, string> target, ThemeDefinition? src)
        {
            if (src == null) return;
            foreach (var (key, _) in ColorFields)
            {
                var val = typeof(ThemeDefinition).GetProperty(key)?.GetValue(src) as string ?? "";
                if (!string.IsNullOrWhiteSpace(val)) target[key] = val;
            }
        }

        /// <summary>Fills the colour boxes + swatches from the active variant's value set.</summary>
        private void PopulateColorBoxes()
        {
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                foreach (var (key, _) in ColorFields)
                {
                    var lv = _lightVals.TryGetValue(key, out var l) ? l : "";
                    var dv = _darkVals.TryGetValue(key,  out var d) ? d : "";
                    if (_lightBoxes.TryGetValue(key, out var lb)) lb.Text = lv;
                    if (_darkBoxes.TryGetValue(key,  out var db)) db.Text = dv;
                    UpdateSwatch(_lightSwatches, key, lv);
                    UpdateSwatch(_darkSwatches,  key, dv);
                }
            }
            finally { _loading = wasLoading; }
            UpdateTrayPreview();
        }

        // ── Per-variant assets ────────────────────────────────────────────────

        private string ActiveLogo
        {
            get => _editingDark ? _darkLogo : _lightLogo;
            set { if (_editingDark) _darkLogo = value; else _lightLogo = value; }
        }

        private string ActiveBgImg
        {
            get => _editingDark ? _darkBgImg : _lightBgImg;
            set { if (_editingDark) _darkBgImg = value; else _lightBgImg = value; }
        }

        private string ActiveTrayC
        {
            get => _editingDark ? _darkTrayC : _lightTrayC;
            set { if (_editingDark) _darkTrayC = value; else _lightTrayC = value; }
        }

        private string ActiveTrayD
        {
            get => _editingDark ? _darkTrayD : _lightTrayD;
            set { if (_editingDark) _darkTrayD = value; else _lightTrayD = value; }
        }

        /// <summary>Asset filename stem — variant-suffixed in per-variant mode so
        /// dark and light images don't overwrite each other in the theme folder.</summary>
        private string AssetBaseName(string baseName) =>
            _perVariantAssets ? $"{baseName}-{(_editingDark ? "dark" : "light")}" : baseName;

        private void PopulateAssetBoxes()
        {
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                LogoPathBox.Text     = ActiveLogo;
                BgImagePathBox.Text  = ActiveBgImg;
                TrayIconConnBox.Text = ActiveTrayC;
                TrayIconDiscBox.Text = ActiveTrayD;
            }
            finally { _loading = wasLoading; }
            RefreshLogoPreview();
        }

        private void PerVariantAssets_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || _loading) return;
            _perVariantAssets = PerVariantAssetsCheck.IsChecked == true;

            if (_perVariantAssets)
            {
                // Seed both variants from the current shared images
                _darkLogo  = _lightLogo  = LogoPathBox.Text.Trim();
                _darkBgImg = _lightBgImg = BgImagePathBox.Text.Trim();
                _darkTrayC = _lightTrayC = TrayIconConnBox.Text.Trim();
                _darkTrayD = _lightTrayD = TrayIconDiscBox.Text.Trim();
            }
            else
            {
                // Collapse to one shared set — the boxes keep the currently
                // shown variant's images as the shared values
                _darkLogo  = _lightLogo  = _darkBgImg = _lightBgImg = "";
                _darkTrayC = _lightTrayC = _darkTrayD = _lightTrayD = "";
            }
            OnEditorChanged();
        }

        private void Variant_Changed(object sender, RoutedEventArgs e)
        {
            // IsChecked="True" in XAML fires this during InitializeComponent
            if (!IsInitialized || _loading) return;
            bool wantDark = VariantDark.IsChecked == true;
            if (wantDark == _editingDark) return;

            _editingDark = wantDark;            // boxes already synced to the old set
            PopulateColorBoxes();
            if (_perVariantAssets) PopulateAssetBoxes();

            // Restyle the app to the newly selected variant right away — this is the
            // Dark/Light preview, so it works for read-only (shared/System) themes too.
            // Switching alone is not an edit — the dirty flag is untouched.
            if (!string.IsNullOrEmpty(_editingName))
                ApplyDraftLive();
        }

        private void CopyAllToLight_Click(object sender, RoutedEventArgs e) => CopyAllVariant(_darkVals, _lightVals, "Light");
        private void CopyAllToDark_Click(object sender, RoutedEventArgs e)  => CopyAllVariant(_lightVals, _darkVals, "Dark");

        /// <summary>
        /// Bulk-copies every colour from one variant into the other. The global Invert
        /// toggle decides raw vs inverted (same per-key clamping the arrows use); the
        /// "Overwrite only empty slots" toggle, when on, fills blank target slots only
        /// and keeps existing values (otherwise the target variant is replaced).
        /// </summary>
        private void CopyAllVariant(Dictionary<string, string> source, Dictionary<string, string> target, string targetName)
        {
            if (_readOnly || string.IsNullOrEmpty(_editingName)) return;
            if (source.Count == 0)
            {
                ThemedMessageDialog.Info(this, $"The other variant has no colours to copy into {targetName}.", "Theme Builder");
                return;
            }

            bool invert    = InvertCopyCheck?.IsChecked == true;
            bool emptyOnly = EmptySlotsOnlyCheck?.IsChecked == true;
            if (!emptyOnly) target.Clear();
            foreach (var (key, _) in ColorFields)
            {
                // Tray slots resolve their fallback so the tray menu is copied too.
                var v = SourceColor(source, key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (emptyOnly && target.TryGetValue(key, out var existing) && !string.IsNullOrWhiteSpace(existing)) continue;
                target[key] = invert ? InvertColorValue(key, v) : v;
            }
            PopulateColorBoxes();
            OnEditorChanged();
        }

        // ── Color rows (built programmatically) — Light + Dark side by side ───────
        private void BuildColorRows()
        {
            // Column header: Light | Dark
            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            AddColorColumns(header);
            var hLight = new TextBlock { Text = "Light", FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(hLight, 1); Grid.SetColumnSpan(hLight, 2);
            var hDark = new TextBlock { Text = "Dark", FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextMuted"),
                HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(hDark, 4); Grid.SetColumnSpan(hDark, 2);
            header.Children.Add(hLight); header.Children.Add(hDark);
            ColorsPanel.Children.Add(header);

            foreach (var (key, label) in ColorFields)
            {
                if (key == "ColorTrayBg")
                {
                    ColorsPanel.Children.Add(new TextBlock
                    {
                        Text       = "Tray menu",
                        FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                        FontSize   = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("TextPrimary"),
                        Margin     = new Thickness(0, 10, 0, 6),
                    });
                }

                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                AddColorColumns(row);

                var lbl = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                    FontSize   = 10,
                    Foreground = (Brush)FindResource("TextMuted"),
                };
                Grid.SetColumn(lbl, 0);
                row.Children.Add(lbl);

                AddColorCell(row, key, dark: false, boxCol: 1, swatchCol: 2);

                // Copy arrows between the two variants
                var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                arrows.Children.Add(MakeArrow("←", "Copy Dark → Light", key, copyToDark: false));
                arrows.Children.Add(MakeArrow("→", "Copy Light → Dark", key, copyToDark: true));
                Grid.SetColumn(arrows, 3);
                row.Children.Add(arrows);

                AddColorCell(row, key, dark: true, boxCol: 4, swatchCol: 5);

                ColorsPanel.Children.Add(row);
            }

            BuildTrayPreview();
            RefreshArrowGlyphs();
        }

        private static void AddColorColumns(Grid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });               // label
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // light box
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // light swatch
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // arrows
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // dark box
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                   // dark swatch
        }

        /// <summary>Builds a hex box + swatch for one variant of one colour key and registers them.</summary>
        private void AddColorCell(Grid row, string key, bool dark, int boxCol, int swatchCol)
        {
            var box = new TextBox
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 10,
                MinWidth   = 64,
                Padding    = new Thickness(6, 3, 4, 3),
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = (key, dark),
            };
            box.TextChanged += ColorBox_TextChanged;
            Grid.SetColumn(box, boxCol);

            var swatch = new Border
            {
                Width           = 28,
                Height          = 22,
                CornerRadius    = new CornerRadius(3),
                Margin          = new Thickness(4, 0, 0, 0),
                Cursor          = Cursors.Hand,
                BorderBrush     = (Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                Tag = (key, dark),
                ToolTip = "Click to pick a colour (move off the popup to grab from screen)",
            };
            swatch.MouseLeftButtonUp += Swatch_Click;
            Grid.SetColumn(swatch, swatchCol);

            row.Children.Add(box);
            row.Children.Add(swatch);
            (dark ? _darkBoxes    : _lightBoxes)[key]    = box;
            (dark ? _darkSwatches : _lightSwatches)[key] = swatch;
        }

        private Button MakeArrow(string glyph, string tip, string key, bool copyToDark)
        {
            var b = new Button
            {
                Content = glyph, FontSize = 11, Width = 22, Height = 20, Padding = new Thickness(0),
                Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand, ToolTip = tip,
                Tag = (key, copyToDark),
            };
            b.Click += Arrow_Click;
            _arrows.Add(b);
            return b;
        }

        /// <summary>
        /// Reflects the global Invert mode in the per-row copy arrows: raw arrows
        /// (← →) when off, double/accent arrows (⇇ ⇉) when on, so the hidden mode is
        /// obvious. The Copy-all buttons keep fixed labels (their direction is explicit).
        /// </summary>
        private void RefreshArrowGlyphs()
        {
            bool inv = InvertCopyCheck?.IsChecked == true;
            var accent = (Brush)FindResource("Accent");
            foreach (var b in _arrows)
            {
                if (b.Tag is not ValueTuple<string, bool> tag) continue;
                bool toDark = tag.Item2;
                b.Content    = inv ? (toDark ? "⇉" : "⇇") : (toDark ? "→" : "←");
                b.FontWeight = inv ? FontWeights.Bold : FontWeights.Normal;
                if (inv) b.Foreground = accent; else b.ClearValue(Control.ForegroundProperty);
                b.ToolTip = (toDark ? "Copy Light → Dark" : "Copy Dark → Light") + (inv ? " (inverted)" : "");
            }
        }

        private void InvertCopy_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            RefreshArrowGlyphs();
        }

        /// <summary>
        /// Inverts a single colour value with the same background-vs-foreground-aware
        /// clamping the bulk auto-generate uses, by running it through
        /// <see cref="ThemeManager.AutoInvertVariant"/> on a one-key throwaway definition.
        /// A blank value stays blank.
        /// </summary>
        private static string InvertColorValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var tmp = new ThemeDefinition();
            typeof(ThemeDefinition).GetProperty(key)?.SetValue(tmp, value);
            var inv = ThemeManager.AutoInvertVariant(tmp);
            return typeof(ThemeDefinition).GetProperty(key)?.GetValue(inv) as string ?? value;
        }

        // Tray colours fall back to main colours when their own slot is blank (same map
        // as ThemeManager.Apply / EffColor). Resolving the fallback lets copy / invert
        // populate the tray menu even when the user left the tray slots empty.
        private static readonly Dictionary<string, string> TrayFallback = new()
        {
            ["ColorTrayBg"]     = "ColorSurface",
            ["ColorTrayHover"]  = "ColorBorder",
            ["ColorTrayText"]   = "ColorTextPrimary",
            ["ColorTrayBorder"] = "ColorBorder",
        };

        /// <summary>The effective source value for a key: its own value, or — for tray
        /// keys — the semantic fallback from the same variant when its own slot is blank.</summary>
        private static string SourceColor(Dictionary<string, string> src, string key)
        {
            if (src.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (TrayFallback.TryGetValue(key, out var fb) &&
                src.TryGetValue(fb, out var fv) && !string.IsNullOrWhiteSpace(fv)) return fv;
            return "";
        }

        private void Arrow_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnly) return;
            if (sender is not Button b || b.Tag is not ValueTuple<string, bool> tag) return;
            var (key, copyToDark) = tag;
            if (!_lightBoxes.TryGetValue(key, out var lb) || !_darkBoxes.TryGetValue(key, out var db)) return;

            // Source is the FROM side (tray slots resolve their fallback so the tray copies
            // even when blank); the global Invert toggle decides raw vs inverted.
            var src = copyToDark ? SourceColor(_lightVals, key) : SourceColor(_darkVals, key);
            if (InvertCopyCheck?.IsChecked == true) src = InvertColorValue(key, src);
            if (copyToDark) db.Text = src;   // → : Light into Dark
            else            lb.Text = src;   // ← : Dark into Light
        }

        /// <summary>
        /// Builds a small rendered facsimile of the tray menu beneath the
        /// ColorTray* rows. It restyles live from the draft colours (with the same
        /// semantic fallbacks the real tray uses) so the user can judge tray
        /// colours without opening the actual menu — which only restyles on Save.
        /// </summary>
        private void BuildTrayPreview()
        {
            ColorsPanel.Children.Add(new TextBlock
            {
                Text       = "Tray menu preview",
                FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                FontSize   = 10,
                Foreground = (Brush)FindResource("TextMuted"),
                Margin     = new Thickness(0, 8, 0, 6),
            });

            var rows = new StackPanel { Margin = new Thickness(4) };
            string[] items = { "MyTunnel", "Connect", "Settings", "Exit" };
            for (int i = 0; i < items.Length; i++)
            {
                bool hover = i == 1;   // one row shown in the hover state
                var text = new TextBlock
                {
                    Text       = items[i],
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize   = 11,
                    Padding    = new Thickness(10, 4, 24, 4),
                };
                _trayMockTexts.Add(text);

                var rowBorder = new Border { Child = text, CornerRadius = new CornerRadius(2) };
                if (hover) _trayMockHoverRow = rowBorder;
                rows.Children.Add(rowBorder);
            }

            _trayMockBorder = new Border
            {
                Child           = rows,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth        = 160,
                Margin          = new Thickness(0, 0, 0, 4),
            };
            ColorsPanel.Children.Add(_trayMockBorder);
        }

        /// <summary>Restyles the tray mock from the active variant's draft colours.</summary>
        private void UpdateTrayPreview()
        {
            if (_trayMockBorder == null) return;

            // Same fallbacks as ThemeManager.Apply: tray bg→surface, hover→border,
            // text→primary text, border→border.
            var bg     = ParseOr(EffColor("ColorTrayBg",     "ColorSurface"),     Color.FromRgb(0x2B, 0x2B, 0x2B));
            var hover  = ParseOr(EffColor("ColorTrayHover",  "ColorBorder"),      Color.FromRgb(0x3A, 0x3A, 0x3A));
            var text   = ParseOr(EffColor("ColorTrayText",   "ColorTextPrimary"), Colors.White);
            var border = ParseOr(EffColor("ColorTrayBorder", "ColorBorder"),      Color.FromRgb(0x55, 0x55, 0x55));

            _trayMockBorder.Background  = new SolidColorBrush(bg);
            _trayMockBorder.BorderBrush = new SolidColorBrush(border);
            foreach (var t in _trayMockTexts) t.Foreground = new SolidColorBrush(text);
            if (_trayMockHoverRow != null) _trayMockHoverRow.Background = new SolidColorBrush(hover);
        }

        /// <summary>Active-variant value for <paramref name="key"/>, else its semantic fallback, else "".</summary>
        private string EffColor(string key, string fallbackKey)
        {
            if (ActiveVals.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (ActiveVals.TryGetValue(fallbackKey, out var f) && !string.IsNullOrWhiteSpace(f)) return f;
            return "";
        }

        private static Color ParseOr(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }

        // ── Theme list ────────────────────────────────────────────────────────
        private void PopulateThemeList()
        {
            BuiltinList.Items.Clear();
            SharedList.Items.Clear();
            CustomList.Items.Clear();

            var active = ThemeManager.Instance.CurrentThemeName;

            // BUILT-IN — only the virtual System theme, embedded in code and read-only
            // (locked). Duplicable so the live Windows palette can seed an editable copy.
            BuiltinList.Items.Add(BuildListItem("__system__", "System (Windows colors)",
                isBuiltin: true, isActive: active is "__system__" or "system"));

            // SHARED — every folder shipped/downloaded into the install's shared_themes\
            // folder. Read-only (locked) but deletable and copyable (Duplicate to edit).
            foreach (var name in ThemeManager.SharedThemeNames())
            {
                var display = ThemeManager.GetThemeDisplayName(name);
                SharedList.Items.Add(BuildListItem(name, display, isBuiltin: true, isActive: name == active));
            }

            // CUSTOM — the user's own themes in %APPDATA%\MasselGUARD\custom_themes\
            var userRoot = ThemeManager.UserThemeRoot;
            List<string> customNames = new();
            if (Directory.Exists(userRoot))
                customNames = Directory.GetDirectories(userRoot)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => File.Exists(Path.Combine(userRoot, n, "theme.json")))
                    .OrderBy(n => n)
                    .ToList();

            foreach (var name in customNames)
            {
                var display = ThemeManager.GetThemeDisplayName(name);
                var item    = BuildListItem(name, display, isBuiltin: false, isActive: name == active);
                CustomList.Items.Add(item);
            }

            NoCustomLabel.Visibility = customNames.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private ListBoxItem BuildListItem(string name, string display, bool isBuiltin, bool isActive)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            if (isActive)
            {
                panel.Children.Add(new TextBlock
                {
                    Text       = "● ",
                    FontSize   = 8,
                    Foreground = (Brush)FindResource("Accent"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            panel.Children.Add(new TextBlock
            {
                Text       = display,
                FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                FontSize   = 11,
                Foreground = (Brush)FindResource(isActive ? "Accent" : "TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (isBuiltin)
                panel.Children.Add(new TextBlock
                {
                    Text       = "  🔒",
                    FontSize   = 9,
                    Foreground = (Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                });

            return new ListBoxItem { Content = panel, Tag = name };
        }

        // ── List selection ────────────────────────────────────────────────────
        private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Single selection across the three lists — clear the other two
            if ((sender as ListBox)?.SelectedItem != null)
            {
                if (sender != BuiltinList) BuiltinList.SelectedItem = null;
                if (sender != SharedList)  SharedList.SelectedItem  = null;
                if (sender != CustomList)  CustomList.SelectedItem  = null;
            }

            var selected = (sender as ListBox)?.SelectedItem as ListBoxItem;
            if (selected == null) return;

            var name = selected.Tag as string ?? "";
            if (!string.Equals(name, _editingName, StringComparison.OrdinalIgnoreCase))
                ResolveDirtyDraft($"Save changes to '{ThemeName.Text}' before switching?");
            LoadTheme(name);
        }

        private void LoadTheme(string name)
        {
            _editingName = name;
            // Editor is read-only for the System theme AND shared themes (shipped/downloaded);
            // only the user's own custom themes are editable. Shared + custom can be deleted.
            _readOnly  = ThemeManager.IsBuiltinTheme(name) || ThemeManager.IsSharedTheme(name);
            _canDelete = !ThemeManager.IsBuiltinTheme(name);
            _dirty       = false;
            _applyTimer?.Stop();

            // Load definition
            ThemeDefinition? def;
            if (name == "__system__")
            {
                // Virtual theme — expose the live Windows palette so it can be
                // inspected in the editor and duplicated into an editable copy.
                def = new ThemeDefinition
                {
                    Name  = "System (Windows colors)",
                    Dark  = ThemeManager.BuildSystemTheme(true),
                    Light = ThemeManager.BuildSystemTheme(false),
                };
            }
            else
                def = ThemeManager.GetThemeMetadata(name);
            _draft = def ?? ThemeDefinition.Default;

            // Split colours into the variant dictionaries
            _darkVals.Clear();
            _lightVals.Clear();
            if (_draft.IsDualVariant)
            {
                FillVariantVals(_darkVals,  _draft.Dark);
                FillVariantVals(_lightVals, _draft.Light);
                // Start on the variant matching the system mode, but prefer one
                // that actually has colours
                _editingDark = ThemeManager.GetSystemIsDark();
                if (_editingDark  && _darkVals.Count  == 0 && _lightVals.Count > 0) _editingDark = false;
                if (!_editingDark && _lightVals.Count == 0 && _darkVals.Count  > 0) _editingDark = true;
            }
            else
            {
                // Legacy flat theme: root colours belong to its Type variant.
                // Saving rewrites it in the unified format — that is the migration.
                bool legacyDark = !_draft.Type.Equals("light", StringComparison.OrdinalIgnoreCase);
                FillVariantVals(legacyDark ? _darkVals : _lightVals, _draft);
                _editingDark = legacyDark;
            }

            // Per-variant asset overrides (logo / background / tray icons in the variant sections)
            _darkLogo   = _draft.Dark?.Logo                  ?? "";
            _lightLogo  = _draft.Light?.Logo                 ?? "";
            _darkBgImg  = _draft.Dark?.BackgroundImage       ?? "";
            _lightBgImg = _draft.Light?.BackgroundImage      ?? "";
            _darkTrayC  = _draft.Dark?.TrayIconConnected     ?? "";
            _lightTrayC = _draft.Light?.TrayIconConnected    ?? "";
            _darkTrayD  = _draft.Dark?.TrayIconDisconnected  ?? "";
            _lightTrayD = _draft.Light?.TrayIconDisconnected ?? "";
            _perVariantAssets = name != "__system__" &&
                (_darkLogo.Length  > 0 || _lightLogo.Length  > 0 ||
                 _darkBgImg.Length > 0 || _lightBgImg.Length > 0 ||
                 _darkTrayC.Length > 0 || _lightTrayC.Length > 0 ||
                 _darkTrayD.Length > 0 || _lightTrayD.Length > 0);

            _loading = true;
            try { PopulateEditor(); }
            finally { _loading = false; }

            // Show/hide panels
            EmptyStateLabel.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility     = Visibility.Visible;
            ReadOnlyBanner.Visibility  = _readOnly ? Visibility.Visible : Visibility.Collapsed;

            // Buttons — System is duplicable (seeds a copy of the live palette)
            // but not exportable (it has no folder on disk). Shared themes are read-only
            // but can still be deleted, duplicated and exported.
            SetEditorReadOnly(_readOnly);
            DuplicateBtn.IsEnabled   = true;
            ExportBtn.IsEnabled      = name != "__system__";
            DeleteThemeBtn.IsEnabled = _canDelete;
            SaveBtn.IsEnabled        = !_readOnly;

            // Editable theme → edits apply live
            LiveIndicator.Visibility = _readOnly ? Visibility.Collapsed : Visibility.Visible;
            StatusLabel.Text = "";

            // Reset undo history to this freshly-loaded (last-saved) state.
            _undo.Clear();
            _redo.Clear();
            _baseline = _readOnly ? null : Snapshot();
            UpdateUndoRedoButtons();
            if (CancelBtn != null) CancelBtn.IsEnabled = false;
        }

        private void PopulateEditor()
        {
            var d = _draft;

            // Identity
            ThemeName.Text    = d.Name;
            AppNameBox.Text   = d.AppName;
            CreatorBox.Text   = d.Creator;
            DescriptionBox.Text = d.Description;

            // Variant pill + colour boxes show the active variant's colours
            VariantDark.IsChecked  = _editingDark;
            VariantLight.IsChecked = !_editingDark;
            PopulateColorBoxes();

            // Typography
            FontFamilyBox.Text          = d.FontFamily;
            FontSizeSlider.Value        = d.FontSize;
            CornerRadiusSlider.Value    = d.CornerRadius;

            // Window
            OpacitySlider.Value         = d.WindowOpacity;
            PanelOpacitySlider.Value    = d.PanelOpacity;
            TitleBarHeightSlider.Value  = d.TitleBarHeight;
            ShowTitleBarIconCheck.IsChecked    = d.ShowTitleBarIcon;
            ShowTitleBarAppNameCheck.IsChecked = d.ShowTitleBarAppName;
            ShowResizeGripCheck.IsChecked      = d.ShowResizeGrip;

            // Status bar
            ShowStatusBarCheck.IsChecked    = d.ShowStatusBar;
            StatusBarHeightSlider.Value     = d.StatusBarHeight;
            ShowStatusWifiCheck.IsChecked   = d.ShowStatusWifi;
            ShowStatusTunnelCheck.IsChecked = d.ShowStatusTunnel;

            // Assets — per-variant mode shows the active variant's images
            PerVariantAssetsCheck.IsChecked = _perVariantAssets;
            LogoPathBox.Text     = _perVariantAssets ? ActiveLogo  : d.Logo;
            LogoWidthBox.Text    = d.LogoWidth.ToString();
            LogoHeightBox.Text   = d.LogoHeight.ToString();
            AppIconPathBox.Text  = d.AppIcon;
            TrayIconConnBox.Text = _perVariantAssets ? ActiveTrayC : d.TrayIconConnected;
            TrayIconDiscBox.Text = _perVariantAssets ? ActiveTrayD : d.TrayIconDisconnected;
            RefreshLogoPreview();

            // Background
            BgImagePathBox.Text   = _perVariantAssets ? ActiveBgImg : d.BackgroundImage;
            BgOpacitySlider.Value = d.BackgroundOpacity;
            (d.BackgroundStretch?.ToLowerInvariant() switch
            {
                "center"  => StretchCenter,
                "tile"    => StretchTile,
                "topleft" => StretchTopLeft,
                _         => StretchFill,
            }).IsChecked = true;

            UpdateSliderLabels();
        }

        private void SetEditorReadOnly(bool readOnly)
        {
            // Identity
            ThemeName.IsReadOnly    = readOnly;
            AppNameBox.IsReadOnly   = readOnly;
            CreatorBox.IsReadOnly   = readOnly;
            DescriptionBox.IsReadOnly = readOnly;
            // Variant pills stay enabled — switching is useful even for read-only
            // built-ins; only the mutating helpers are locked.
            CopyAllLightBtn.IsEnabled = !readOnly;
            CopyAllDarkBtn.IsEnabled  = !readOnly;
            InvertCopyCheck.IsEnabled     = !readOnly;
            EmptySlotsOnlyCheck.IsEnabled = !readOnly;

            // Color boxes (both variants)
            foreach (var box in _lightBoxes.Values) box.IsReadOnly = readOnly;
            foreach (var box in _darkBoxes.Values)  box.IsReadOnly = readOnly;
            foreach (var sw in _lightSwatches.Values) sw.Cursor = readOnly ? Cursors.Arrow : Cursors.Hand;
            foreach (var sw in _darkSwatches.Values)  sw.Cursor = readOnly ? Cursors.Arrow : Cursors.Hand;

            // Other controls
            FontFamilyBox.IsEnabled  = !readOnly;
            FontSizeSlider.IsEnabled = !readOnly;
            CornerRadiusSlider.IsEnabled = !readOnly;
            OpacitySlider.IsEnabled  = !readOnly;
            PanelOpacitySlider.IsEnabled = !readOnly;
            PerVariantAssetsCheck.IsEnabled = !readOnly;
            TitleBarHeightSlider.IsEnabled = !readOnly;
            ShowTitleBarIconCheck.IsEnabled    = !readOnly;
            ShowTitleBarAppNameCheck.IsEnabled = !readOnly;
            ShowResizeGripCheck.IsEnabled      = !readOnly;
            ShowStatusBarCheck.IsEnabled    = !readOnly;
            StatusBarHeightSlider.IsEnabled = !readOnly;
            ShowStatusWifiCheck.IsEnabled   = !readOnly;
            ShowStatusTunnelCheck.IsEnabled = !readOnly;
            LogoPathBox.IsReadOnly     = readOnly;
            LogoWidthBox.IsReadOnly    = readOnly;
            LogoHeightBox.IsReadOnly   = readOnly;
            AppIconPathBox.IsReadOnly  = readOnly;
            TrayIconConnBox.IsReadOnly = readOnly;
            TrayIconDiscBox.IsReadOnly = readOnly;
            BgImagePathBox.IsReadOnly = readOnly;
            BgOpacitySlider.IsEnabled = !readOnly;
            StretchFill.IsEnabled    = !readOnly;
            StretchCenter.IsEnabled  = !readOnly;
            StretchTile.IsEnabled    = !readOnly;
            StretchTopLeft.IsEnabled = !readOnly;
        }

        // ── Color picker handlers ─────────────────────────────────────────────
        private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            if (sender is not TextBox box || box.Tag is not ValueTuple<string, bool> tag) return;
            var (key, dark) = tag;
            var vals     = dark ? _darkVals     : _lightVals;
            var swatches = dark ? _darkSwatches : _lightSwatches;
            UpdateSwatch(swatches, key, box.Text);
            var val = box.Text.Trim();
            if (string.IsNullOrEmpty(val)) vals.Remove(key);
            else                           vals[key] = val;
            UpdateTrayPreview();   // reads the active (pill) variant
            OnEditorChanged();
        }

        private static void UpdateSwatch(Dictionary<string, Border> swatches, string key, string hex)
        {
            if (!swatches.TryGetValue(key, out var swatch)) return;
            try
            {
                var color = string.IsNullOrWhiteSpace(hex)
                    ? Colors.Transparent
                    : (Color)ColorConverter.ConvertFromString(hex);
                swatch.Background = new SolidColorBrush(color);
            }
            catch
            {
                swatch.Background = new SolidColorBrush(Colors.Transparent);
            }
        }

        private void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_readOnly) return;
            if (sender is not Border swatch || swatch.Tag is not ValueTuple<string, bool> tag) return;
            var (key, dark) = tag;
            var boxes = dark ? _darkBoxes : _lightBoxes;

            Color current = Colors.White;
            if (boxes.TryGetValue(key, out var box) && !string.IsNullOrWhiteSpace(box.Text))
            {
                try { current = (Color)ColorConverter.ConvertFromString(box.Text); }
                catch { }
            }

            var dlg = new ColorPickerDialog(current) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Selected is Color c)
            {
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                if (boxes.TryGetValue(key, out var tb))
                    tb.Text = hex;   // triggers ColorBox_TextChanged → live apply + swatch
            }
        }

        // ── Field change handlers — every edit feeds the debounced live apply ──
        private void Field_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            // In per-variant mode the asset boxes edit the active variant
            if (_perVariantAssets)
            {
                if (sender == LogoPathBox)     ActiveLogo  = LogoPathBox.Text.Trim();
                if (sender == BgImagePathBox)  ActiveBgImg = BgImagePathBox.Text.Trim();
                if (sender == TrayIconConnBox) ActiveTrayC = TrayIconConnBox.Text.Trim();
                if (sender == TrayIconDiscBox) ActiveTrayD = TrayIconDiscBox.Text.Trim();
            }
            OnEditorChanged();
        }

        private void Toggle_Changed(object sender, RoutedEventArgs e)    => OnEditorChanged();
        private void BgStretch_Changed(object sender, RoutedEventArgs e) => OnEditorChanged();

        private void FontFamily_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            OnEditorChanged();
        }

        private void FontFamily_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            OnEditorChanged();   // catches free-typed family names
        }

        // Sliders with an initial Value in XAML fire ValueChanged DURING
        // InitializeComponent, before their label elements exist — the
        // IsInitialized guard skips those; UpdateSliderLabels() sets the
        // labels once the editor is populated.
        private void FontSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            FontSizeLabel.Text = $"{(int)FontSizeSlider.Value} pt";
            OnEditorChanged();
        }

        private void CornerRadius_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            CornerRadiusLabel.Text = $"{(int)CornerRadiusSlider.Value} px";
            OnEditorChanged();
        }

        private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            OpacityLabel.Text = $"{OpacitySlider.Value:P0}";
            OnEditorChanged();
        }

        private void PanelOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            PanelOpacityLabel.Text = $"{PanelOpacitySlider.Value:P0}";
            OnEditorChanged();
        }

        private void TitleBarHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            TitleBarHeightLabel.Text = $"{(int)TitleBarHeightSlider.Value} px";
            OnEditorChanged();
        }

        private void StatusBarHeight_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            StatusBarHeightLabel.Text = $"{(int)StatusBarHeightSlider.Value} px";
            OnEditorChanged();
        }

        private void BgOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized) return;
            BgOpacityLabel.Text = $"{BgOpacitySlider.Value:P0}";
            OnEditorChanged();
        }

        private void UpdateSliderLabels()
        {
            FontSizeLabel.Text        = $"{(int)FontSizeSlider.Value} pt";
            CornerRadiusLabel.Text    = $"{(int)CornerRadiusSlider.Value} px";
            OpacityLabel.Text         = $"{OpacitySlider.Value:P0}";
            PanelOpacityLabel.Text    = $"{PanelOpacitySlider.Value:P0}";
            TitleBarHeightLabel.Text  = $"{(int)TitleBarHeightSlider.Value} px";
            StatusBarHeightLabel.Text = $"{(int)StatusBarHeightSlider.Value} px";
            BgOpacityLabel.Text       = $"{BgOpacitySlider.Value:P0}";
        }

        // ── Asset browsers ────────────────────────────────────────────────────
        private void BrowseLogo_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseImage("Logo image|*.png;*.jpg;*.jpeg;*.bmp;*.svg");
            if (path != null) { LogoPathBox.Text = CopyAssetToTheme(path, AssetBaseName("logo")); RefreshLogoPreview(); }
        }
        private void ClearLogo_Click(object sender, RoutedEventArgs e)
        {
            LogoPathBox.Text = "";
            LogoPreviewBorder.Visibility = Visibility.Collapsed;
        }

        private void BrowseIcon_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseImage("App icon|*.ico;*.png;*.bmp;*.jpg;*.jpeg");
            if (path != null) AppIconPathBox.Text = CopyAssetToTheme(path, "icon");
        }
        private void ClearIcon_Click(object sender, RoutedEventArgs e) => AppIconPathBox.Text = "";

        private void BrowseTrayConn_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseImage("Tray icon|*.ico;*.png;*.bmp;*.jpg;*.jpeg");
            if (path != null) TrayIconConnBox.Text = CopyAssetToTheme(path, AssetBaseName("tray-connected"));
        }
        private void ClearTrayConn_Click(object sender, RoutedEventArgs e) => TrayIconConnBox.Text = "";

        private void BrowseTrayDisc_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseImage("Tray icon|*.ico;*.png;*.bmp;*.jpg;*.jpeg");
            if (path != null) TrayIconDiscBox.Text = CopyAssetToTheme(path, AssetBaseName("tray-disconnected"));
        }
        private void ClearTrayDisc_Click(object sender, RoutedEventArgs e) => TrayIconDiscBox.Text = "";

        private void BrowseBg_Click(object sender, RoutedEventArgs e)
        {
            var path = BrowseImage("Background image|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff");
            if (path != null) BgImagePathBox.Text = CopyAssetToTheme(path, AssetBaseName("bg"));
        }
        private void ClearBg_Click(object sender, RoutedEventArgs e) => BgImagePathBox.Text = "";

        private static string? BrowseImage(string filter)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>Folder of the theme currently being edited, at its real location —
        /// <c>shared_themes\</c> for shared themes, <c>custom_themes\</c> for the user's own.
        /// Edits/saves/deletes target this so shared themes update in place.</summary>
        private string EditingThemeDir => ThemeManager.ThemeFolder(_editingName);

        /// <summary>
        /// Copies a picked file into the current theme's folder (creating it if needed)
        /// and returns just the filename so theme.json stores a relative path.
        /// </summary>
        private string CopyAssetToTheme(string srcPath, string destBaseName)
        {
            if (string.IsNullOrEmpty(_editingName) || _readOnly) return Path.GetFileName(srcPath);
            try
            {
                var ext      = Path.GetExtension(srcPath);
                var fileName = destBaseName + ext;
                var themeDir = EditingThemeDir;
                Directory.CreateDirectory(themeDir);
                File.Copy(srcPath, Path.Combine(themeDir, fileName), overwrite: true);
                return fileName;
            }
            catch { return Path.GetFileName(srcPath); }
        }

        private void RefreshLogoPreview()
        {
            var logoFile = LogoPathBox?.Text ?? "";
            if (string.IsNullOrWhiteSpace(logoFile) || string.IsNullOrEmpty(_editingName))
            {
                LogoPreviewBorder.Visibility = Visibility.Collapsed;
                return;
            }
            var full = Path.Combine(ThemeManager.UserThemeRoot, _editingName, logoFile);
            if (!File.Exists(full)) full = Path.Combine(ThemeManager.ThemeFolder(_editingName), logoFile);
            if (!File.Exists(full)) { LogoPreviewBorder.Visibility = Visibility.Collapsed; return; }

            try
            {
                // Uncached + fully-loaded: keeps the file unlocked for overwrites and
                // shows the new content when logo.png is replaced under the same name
                LogoPreviewImg.Source        = ThemeManager.LoadImageUncached(full);
                LogoPreviewBorder.Visibility = Visibility.Visible;
            }
            catch { LogoPreviewBorder.Visibility = Visibility.Collapsed; }
        }

        // ── Collect draft from UI ─────────────────────────────────────────────
        private ThemeDefinition CollectDraft()
        {
            var d = new ThemeDefinition
            {
                Name        = ThemeName.Text.Trim(),
                AppName     = AppNameBox.Text.Trim(),
                Creator     = CreatorBox.Text.Trim(),
                Description = DescriptionBox.Text.Trim(),
                // ApplyPreview derives dark/light from Type, so the live preview
                // always shows the variant currently being edited
                Type        = _editingDark ? "dark" : "light",

                FontFamily    = FontFamilyBox.Text.Trim(),
                FontSize      = FontSizeSlider.Value,
                CornerRadius  = (int)CornerRadiusSlider.Value,

                WindowOpacity      = Math.Round(OpacitySlider.Value, 2),
                PanelOpacity       = Math.Round(PanelOpacitySlider.Value, 2),
                TitleBarHeight     = (int)TitleBarHeightSlider.Value,
                ShowTitleBarIcon   = ShowTitleBarIconCheck.IsChecked == true,
                ShowTitleBarAppName= ShowTitleBarAppNameCheck.IsChecked == true,
                ShowResizeGrip     = ShowResizeGripCheck.IsChecked == true,

                ShowStatusBar     = ShowStatusBarCheck.IsChecked == true,
                StatusBarHeight   = (int)StatusBarHeightSlider.Value,
                ShowStatusWifi    = ShowStatusWifiCheck.IsChecked == true,
                ShowStatusTunnel  = ShowStatusTunnelCheck.IsChecked == true,

                Logo         = LogoPathBox.Text.Trim(),
                LogoWidth    = int.TryParse(LogoWidthBox.Text,  out var lw) ? lw : 28,
                LogoHeight   = int.TryParse(LogoHeightBox.Text, out var lh) ? lh : 28,
                AppIcon      = AppIconPathBox.Text.Trim(),
                TrayIconConnected    = TrayIconConnBox.Text.Trim(),
                TrayIconDisconnected = TrayIconDiscBox.Text.Trim(),

                BackgroundImage   = BgImagePathBox.Text.Trim(),
                BackgroundStretch = GetCheckedTag(StretchFill, StretchCenter, StretchTile, StretchTopLeft),
                BackgroundOpacity = Math.Round(BgOpacitySlider.Value, 2),
            };

            // Colors via reflection — the live preview uses the variant the pill selects.
            foreach (var (key, _) in ColorFields)
            {
                if (!ActiveVals.TryGetValue(key, out var v)) continue;
                typeof(ThemeDefinition).GetProperty(key)?.SetValue(d, v);
            }

            return d;
        }

        private static string GetCheckedTag(params RadioButton[] buttons)
        {
            foreach (var rb in buttons)
                if (rb.IsChecked == true) return rb.Tag as string ?? "";
            return "";
        }

        // ── New theme ─────────────────────────────────────────────────────────
        private void NewTheme_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new NewThemeDialog { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var folderName = SanitizeFolderName(dlg.FolderName);
            if (string.IsNullOrEmpty(folderName)) return;

            ThemeDefinition seed;
            string? lightBg = null, darkBg = null;

            if (dlg.FromImage)
            {
                try
                {
                    // Seed each variant from its own picture (light → Light, dark → Dark).
                    seed = new ThemeDefinition
                    {
                        Light = ThemePalette.FromImage(dlg.LightImagePath, out _),
                        Dark  = ThemePalette.FromImage(dlg.DarkImagePath,  out _),
                    };
                }
                catch (Exception ex)
                {
                    ThemedMessageDialog.Info(this, $"Could not build a palette from the image:\n{ex.Message}",
                        "Theme Builder");
                    return;
                }
                seed.Name = Path.GetFileNameWithoutExtension(dlg.LightImagePath);
                if (dlg.UseAsBackground) { lightBg = dlg.LightImagePath; darkBg = dlg.DarkImagePath; }
            }
            else
            {
                // Clone the active definition (serialize round-trip) — mutating
                // ThemeManager.Instance.Current directly would rename the live theme.
                var current = ThemeManager.Instance.Current ?? ThemeDefinition.Default;
                seed = JsonSerializer.Deserialize<ThemeDefinition>(
                           JsonSerializer.Serialize(current)) ?? new ThemeDefinition();
                seed.Name = "New Theme";
            }

            seed.Creator     = "";
            seed.Description = "";

            CreateAndEditTheme(folderName, seed, lightBg: lightBg, darkBg: darkBg);
        }

        /// <summary>Copies an image into the theme folder as that variant's window background.</summary>
        private static void CopyVariantBackground(string dir, ThemeDefinition? variant, string? src, string stem)
        {
            if (variant == null || string.IsNullOrEmpty(src) || !File.Exists(src)) return;
            try
            {
                var fileName = stem + Path.GetExtension(src);
                File.Copy(src, Path.Combine(dir, fileName), overwrite: true);
                variant.BackgroundImage   = fileName;
                variant.BackgroundStretch = "fill";
                variant.BackgroundOpacity = 0.18;
            }
            catch { /* theme still works without the per-variant background */ }
        }

        private static string SanitizeFolderName(string raw) =>
            raw.Trim().ToLowerInvariant()
               .Replace(' ', '-')
               .Replace('\\', '-').Replace('/', '-');

        // ── Duplicate ─────────────────────────────────────────────────────────
        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_editingName)) return;

            bool isSystem = _editingName == "__system__";
            var suggested = (isSystem ? "system" : _editingName) + "-copy";
            var dlg = new InputDialog("Duplicate theme", "Enter a folder name for the copy:", suggested);
            dlg.Owner = this;
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            var folderName = SanitizeFolderName(dlg.Value);
            if (string.IsNullOrEmpty(folderName)) return;

            ThemeDefinition seed;
            if (isSystem)
            {
                // Snapshot the live Windows palette into both variants
                seed = new ThemeDefinition
                {
                    Dark  = ThemeManager.BuildSystemTheme(true),
                    Light = ThemeManager.BuildSystemTheme(false),
                };
                seed.Name = "System (copy)";
            }
            else
            {
                // Prefer the on-disk definition so a dual-variant theme keeps BOTH
                // colour sections in the copy (CollectDraft is flat — active variant
                // only). Unsaved edits are not part of the duplicate.
                seed = ThemeManager.GetThemeMetadata(_editingName) ?? CollectDraft();
                seed.Name = ThemeManager.GetThemeDisplayName(_editingName) + " (copy)";
            }

            CreateAndEditTheme(folderName, seed);

            // Copy the image assets too — theme.json alone would leave the copy
            // without its logo / background / icon files.
            if (!isSystem)
            {
                try
                {
                    var src = ThemeManager.ThemeFolder(_editingName);
                    var dst = Path.Combine(ThemeManager.UserThemeRoot, folderName);
                    if (Directory.Exists(src) && Directory.Exists(dst))
                        foreach (var f in Directory.GetFiles(src))
                            if (!Path.GetFileName(f).Equals("theme.json", StringComparison.OrdinalIgnoreCase))
                                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                }
                catch { /* copy still usable without assets */ }
            }
        }

        private void CreateAndEditTheme(string folderName, ThemeDefinition seed,
            string? bgImageSource = null, string? lightBg = null, string? darkBg = null)
        {
            var dir = Path.Combine(ThemeManager.UserThemeRoot, folderName);
            if (Directory.Exists(dir))
            {
                ThemedMessageDialog.Info(this, $"A theme named '{folderName}' already exists.", "Theme Builder");
                return;
            }

            Directory.CreateDirectory(dir);

            // Optional: ship a single seeding image as the theme's window background (root level)
            if (!string.IsNullOrEmpty(bgImageSource))
            {
                try
                {
                    var fileName = "bg" + Path.GetExtension(bgImageSource);
                    File.Copy(bgImageSource, Path.Combine(dir, fileName), overwrite: true);
                    seed.BackgroundImage   = fileName;
                    seed.BackgroundStretch = "fill";
                    seed.BackgroundOpacity = 0.18;
                }
                catch { /* theme still works without the background image */ }
            }

            // Optional: per-variant backgrounds (two-image seeding)
            CopyVariantBackground(dir, seed.Light, lightBg, "bg-light");
            CopyVariantBackground(dir, seed.Dark,  darkBg,  "bg-dark");

            WriteThemeJson(dir, seed);
            PopulateThemeList();

            // Select the new theme in custom list
            foreach (ListBoxItem item in CustomList.Items)
                if (item.Tag as string == folderName)
                {
                    CustomList.SelectedItem = item;
                    break;
                }
        }

        // ── Delete theme ──────────────────────────────────────────────────────
        private void DeleteTheme_Click(object sender, RoutedEventArgs e)
        {
            if (!_canDelete || string.IsNullOrEmpty(_editingName)) return;

            var display = ThemeManager.GetThemeDisplayName(_editingName);
            if (!ThemedMessageDialog.Confirm(this,
                    $"Delete theme '{display}'?\n\nThis cannot be undone.", "Delete theme"))
                return;

            var dir = EditingThemeDir;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex)
            {
                ThemedMessageDialog.Info(this, $"Could not delete theme folder:\n{ex.Message}", "Delete theme");
                return;
            }

            // Switch away if this was the active theme
            if (ThemeManager.Instance.CurrentThemeName == _editingName)
            {
                _main.ConfigSvc.Config.ActiveTheme = "__system__";
                ThemeManager.Instance.LoadSystem(ThemeManager.GetSystemIsDark());
                _main.ConfigSvc.Save();
            }

            _editingName = "";
            EditorPanel.Visibility     = Visibility.Collapsed;
            EmptyStateLabel.Visibility = Visibility.Visible;
            DeleteThemeBtn.IsEnabled   = false;
            SaveBtn.IsEnabled          = false;
            DuplicateBtn.IsEnabled     = false;
            ExportBtn.IsEnabled        = false;
            LiveIndicator.Visibility   = Visibility.Collapsed;
            StatusLabel.Text           = "";
            _dirty = false;

            PopulateThemeList();
        }

        // ── Save (= save + activate; the builder stays open) ──────────────────
        private void Save_Click(object sender, RoutedEventArgs e) => TrySaveDraft();

        private bool TrySaveDraft()
        {
            if (_readOnly || string.IsNullOrEmpty(_editingName))
            {
                ThemedMessageDialog.Info(this, "System and shared themes can't be saved. Use Duplicate to create an editable copy.",
                    "Theme Builder");
                return false;
            }

            var draft = CollectDraft();

            // Validate name
            if (string.IsNullOrWhiteSpace(draft.Name))
            {
                ThemedMessageDialog.Info(this, "Theme name cannot be empty.", "Theme Builder");
                return false;
            }

            _applyTimer.Stop();

            var dir = EditingThemeDir;
            Directory.CreateDirectory(dir);
            WriteUnifiedThemeJson(dir, draft, _darkVals, _lightVals,
                _perVariantAssets ? VariantAssetMap(dark: true)  : null,
                _perVariantAssets ? VariantAssetMap(dark: false) : null);

            // Reload from disk and activate
            ThemeManager.Instance.Load(_editingName, ThemeManager.GetSystemIsDark());
            _main.ConfigSvc.Config.ActiveTheme = _editingName;
            _main.ConfigSvc.Save();

            _dirty = false;
            if (CancelBtn != null) CancelBtn.IsEnabled = false;   // current == last saved
            StatusLabel.Text = "Saved ✓";
            PopulateThemeList();

            // The committed load above used the system mode; while the builder is
            // open, keep showing the variant being edited.
            ApplyDraftLive();
            return true;
        }

        /// <summary>
        /// Writes the unified dual-variant format: structural settings at root,
        /// colour fields in "dark"/"light" sections (only non-empty values, and a
        /// section is omitted entirely when its variant has no colours — the app
        /// auto-generates the missing side at load time).
        /// </summary>
        /// <summary>Per-variant asset filenames keyed by their theme.json field name.</summary>
        private Dictionary<string, string> VariantAssetMap(bool dark) => new()
        {
            ["logo"]                 = dark ? _darkLogo  : _lightLogo,
            ["backgroundImage"]      = dark ? _darkBgImg : _lightBgImg,
            ["trayIconConnected"]    = dark ? _darkTrayC : _lightTrayC,
            ["trayIconDisconnected"] = dark ? _darkTrayD : _lightTrayD,
        };

        private static void WriteUnifiedThemeJson(string dir, ThemeDefinition root,
            Dictionary<string, string> dark, Dictionary<string, string> light,
            Dictionary<string, string>? darkAssets = null,
            Dictionary<string, string>? lightAssets = null)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented        = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var node = JsonSerializer.SerializeToNode(root, opts)!.AsObject();

            // Root carries structural settings only
            foreach (var (key, _) in ColorFields)
                node.Remove(JsonNamingPolicy.CamelCase.ConvertName(key));
            node.Remove("type");   // obsolete in the unified format
            node.Remove("dark");
            node.Remove("light");

            // In per-variant mode the image assets live in the variant sections;
            // the root copies (active variant, via CollectDraft) would shadow them.
            bool perVariantAssets = darkAssets != null || lightAssets != null;
            if (perVariantAssets)
            {
                node.Remove("logo");
                node.Remove("backgroundImage");
                node.Remove("trayIconConnected");
                node.Remove("trayIconDisconnected");
            }

            static JsonObject? Section(Dictionary<string, string> vals,
                Dictionary<string, string>? assets)
            {
                var o = new JsonObject();
                foreach (var (k, v) in vals)
                    if (!string.IsNullOrWhiteSpace(v))
                        o[JsonNamingPolicy.CamelCase.ConvertName(k)] = v;
                if (assets != null)
                    foreach (var (k, v) in assets)
                        if (!string.IsNullOrWhiteSpace(v))
                            o[k] = v;
                return o.Count > 0 ? o : null;
            }

            if (Section(dark,  darkAssets)  is { } d) node["dark"]  = d;
            if (Section(light, lightAssets) is { } l) node["light"] = l;

            File.WriteAllText(Path.Combine(dir, "theme.json"),
                node.ToJsonString(opts), System.Text.Encoding.UTF8);
        }

        // ── Export / Import (zip) ─────────────────────────────────────────────
        private void ExportTheme_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_editingName) || _editingName == "__system__") return;
            ResolveDirtyDraft($"Save changes to '{ThemeName.Text}' before exporting?");

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _editingName + ".zip",
                Filter   = "Theme zip|*.zip",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var themeDir = ThemeManager.ThemeFolder(_editingName);
                // Bundle the theme font so the importing machine renders correctly
                // even without the font installed — the app loads *.ttf/*.otf from
                // the theme folder as WPF private fonts.
                TryEmbedThemeFont(themeDir);
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                ZipFile.CreateFromDirectory(themeDir, dlg.FileName);
                StatusLabel.Text = "Exported ✓";
            }
            catch (Exception ex)
            {
                ThemedMessageDialog.Info(this, $"Export failed:\n{ex.Message}", "Theme Builder");
            }
        }

        /// <summary>Fonts shipped with every Windows installation — no point bundling.</summary>
        private static readonly string[] UniversalFonts =
        {
            "Segoe UI", "Arial", "Calibri", "Cambria", "Consolas", "Courier New",
            "Georgia", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana",
        };

        /// <summary>
        /// Copies the theme's font files (regular + bold/italic faces) from the
        /// Windows font registry into the theme folder, so an exported zip carries
        /// the typeface with it. Universal Windows fonts are skipped.
        /// </summary>
        private void TryEmbedThemeFont(string themeDir)
        {
            try
            {
                var family = (ThemeManager.GetThemeMetadata(_editingName)?.FontFamily ?? "").Trim();
                if (family.Length == 0) return;
                if (UniversalFonts.Any(u => u.Equals(family, StringComparison.OrdinalIgnoreCase))) return;
                // Already embedded?
                if (Directory.EnumerateFiles(themeDir).Any(f =>
                        f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                    return;

                var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

                void CopyFromRegistry(Microsoft.Win32.RegistryKey? key)
                {
                    if (key == null) return;
                    foreach (var valName in key.GetValueNames())
                    {
                        // Entries look like "Comic Sans MS Bold (TrueType)" — the
                        // family-prefix match also picks up the bold/italic faces.
                        if (!valName.StartsWith(family, StringComparison.OrdinalIgnoreCase)) continue;
                        if (key.GetValue(valName) is not string file || file.Length == 0) continue;

                        var src = Path.IsPathRooted(file) ? file : Path.Combine(fontsDir, file);
                        var ext = Path.GetExtension(src);
                        if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                            !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(src)) continue;

                        File.Copy(src, Path.Combine(themeDir, Path.GetFileName(src)), overwrite: true);
                    }
                }

                using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                CopyFromRegistry(machine);
                using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                CopyFromRegistry(user);
            }
            catch { /* export still works without the bundled font */ }
        }

        private void ImportTheme_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Theme zip|*.zip" };
            if (dlg.ShowDialog() != true) return;

            var folderName = SanitizeFolderName(Path.GetFileNameWithoutExtension(dlg.FileName));
            if (string.IsNullOrEmpty(folderName)) return;

            var dir = Path.Combine(ThemeManager.UserThemeRoot, folderName);
            if (Directory.Exists(dir))
            {
                ThemedMessageDialog.Info(this, $"A theme named '{folderName}' already exists.", "Theme Builder");
                return;
            }

            try
            {
                ZipFile.ExtractToDirectory(dlg.FileName, dir);
                if (!File.Exists(Path.Combine(dir, "theme.json")))
                {
                    Directory.Delete(dir, recursive: true);
                    ThemedMessageDialog.Info(this, "The zip does not contain a theme.json at its root.", "Theme Builder");
                    return;
                }

                PopulateThemeList();
                foreach (ListBoxItem item in CustomList.Items)
                    if (item.Tag as string == folderName)
                    {
                        CustomList.SelectedItem = item;
                        break;
                    }
                StatusLabel.Text = "Imported ✓";
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { }
                ThemedMessageDialog.Info(this, $"Import failed:\n{ex.Message}", "Theme Builder");
            }
        }

        // ── Write theme.json ──────────────────────────────────────────────────
        private static void WriteThemeJson(string dir, ThemeDefinition def)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented        = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var json = JsonSerializer.Serialize(def, opts);
            File.WriteAllText(Path.Combine(dir, "theme.json"), json, System.Text.Encoding.UTF8);
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _applyTimer.Stop();
            ResolveDirtyDraft($"Save changes to '{ThemeName.Text}' before closing?");
            // Restore the committed theme — a no-op when a save just applied it,
            // a revert when live edits were discarded.
            _main.ApplyThemeFromConfig();
            base.OnClosing(e);
        }
    }

    // ── Themed dialog base ────────────────────────────────────────────────────
    // Shared chrome for the builder's pop-ups: the "System (Windows colors)" palette
    // (ThemeManager.ApplySystemTo) + custom borderless chrome (AllowsTransparency,
    // WindowBg/Accent outer border, a Surface title bar with ✕ + DragMove). The palette
    // is the system one (NOT the live draft), so these dialogs stay readable even while a
    // half-finished draft theme is applied to the app. Subclasses build a body element and
    // call SetThemedContent; results are reported via properties, never DialogResult — that
    // throws on AllowsTransparency windows.
    internal abstract class ThemedDialog : Window
    {
        protected ThemedDialog()
        {
            ThemeManager.ApplySystemTo(Resources);
            SizeToContent         = SizeToContent.WidthAndHeight;
            ResizeMode            = ResizeMode.NoResize;
            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            ShowInTaskbar         = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        protected Brush      B(string key)  => (Brush)FindResource(key);
        protected FontFamily ThemeFont()    => (FontFamily)FindResource("Theme.FontFamily");

        /// <summary>Wraps the body in the standard title bar + outer border and assigns Content.</summary>
        protected void SetThemedContent(string title, UIElement body)
        {
            Title = title;

            var titleText = new TextBlock
            {
                Text = title, FontFamily = ThemeFont(), FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = B("Accent"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            };
            var closeBtn = new Button
            {
                Content = "✕", FontSize = 12, Foreground = B("TextMuted"), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeBtn.Click += (_, _) => Close();
            var titleGrid = new Grid();
            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            var titleBar = new Border
            {
                Background = B("Surface"), Height = 40,
                CornerRadius = (CornerRadius)FindResource("Theme.CornerRadiusTop"), Child = titleGrid,
            };
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(titleBar, 0);
            Grid.SetRow(body, 1);
            rootGrid.Children.Add(titleBar);
            rootGrid.Children.Add(body);

            Content = new Border
            {
                Background = B("WindowBg"), BorderBrush = B("Accent"), BorderThickness = new Thickness(1),
                CornerRadius = (CornerRadius)FindResource("Theme.CornerRadius"), Child = rootGrid,
            };
        }
    }

    // ── Simple text-input dialog ──────────────────────────────────────────────
    // System-palette themed (see ThemedDialog) so it matches the rest of the builder
    // while staying readable under a half-finished live draft theme.
    internal class InputDialog : ThemedDialog
    {
        private readonly TextBox _box;
        public string Value     => _box.Text;
        public bool   Confirmed { get; private set; }

        public InputDialog(string title, string prompt, string initial)
        {
            _box = new TextBox
            {
                Text = initial,
                Background = B("CardBg"), Foreground = B("TextPrimary"),
                BorderBrush = B("BorderColor"), CaretBrush = B("TextPrimary"),
                FontFamily = ThemeFont(), FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 8, 0, 12),
            };
            _box.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) Accept(); };

            var ok = new Button
            {
                Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(20, 6, 20, 6),
                HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)FindResource("SuccessBtn"),
            };
            ok.Click += (_, _) => Accept();

            var stack = new StackPanel { Margin = new Thickness(18), MinWidth = 320 };
            stack.Children.Add(new TextBlock
            {
                Text = prompt, TextWrapping = TextWrapping.Wrap,
                FontFamily = ThemeFont(), FontSize = 12, Foreground = B("TextPrimary"),
            });
            stack.Children.Add(_box);
            stack.Children.Add(ok);

            SetThemedContent(title, stack);
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }

        private void Accept() { Confirmed = true; Close(); }
    }

    // ── New-theme dialog: folder name + seeding choice (current theme / image) ──
    // System-palette themed (see ThemedDialog).
    internal class NewThemeDialog : ThemedDialog
    {
        private readonly TextBox     _nameBox;
        private readonly RadioButton _fromCurrent;
        private readonly RadioButton _fromImage;
        private readonly TextBox     _lightImageBox;
        private readonly Button      _lightBrowse;
        private readonly TextBox     _darkImageBox;
        private readonly Button      _darkBrowse;
        private readonly CheckBox    _bgCheck;

        public bool   Confirmed       { get; private set; }
        public string FolderName      => _nameBox.Text;
        public bool   FromImage       => _fromImage.IsChecked == true;
        public string LightImagePath  => _lightImageBox.Text;
        public string DarkImagePath   => _darkImageBox.Text;
        public bool   UseAsBackground => _bgCheck.IsChecked == true;

        public NewThemeDialog()
        {
            // ApplySystemTo only fills brushes, not implicit control styles, so every
            // TextBlock / TextBox / RadioButton / CheckBox is coloured explicitly here.
            TextBlock Caption(string text, double size, Thickness margin) => new()
            {
                Text = text, FontSize = size, FontFamily = ThemeFont(),
                Foreground = B("TextMuted"), Margin = margin, TextWrapping = TextWrapping.Wrap,
            };
            TextBox Field(double size) => new()
            {
                Background = B("CardBg"), Foreground = B("TextPrimary"),
                BorderBrush = B("BorderColor"), CaretBrush = B("TextPrimary"),
                FontFamily = ThemeFont(), FontSize = size, Padding = new Thickness(6, 4, 6, 4),
            };
            RadioButton Radio(string text) => new()
            {
                Content = text, FontSize = 12, FontFamily = ThemeFont(),
                Foreground = B("TextPrimary"), Margin = new Thickness(0, 0, 0, 4),
            };

            var stack = new StackPanel { Margin = new Thickness(18), Width = 420 };

            stack.Children.Add(Caption("Folder name (lowercase, no spaces — e.g. my-theme):", 12, new Thickness(0, 0, 0, 4)));
            _nameBox = Field(12);
            _nameBox.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(_nameBox);

            stack.Children.Add(Caption("Start from:", 12, new Thickness(0, 0, 0, 4)));
            _fromCurrent = Radio("Current theme — tweak what you see now");
            _fromCurrent.IsChecked = true;
            _fromImage = Radio("Light & dark images — extract a palette from each picture");
            _fromImage.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(_fromCurrent);
            stack.Children.Add(_fromImage);

            // Two image rows (enabled only when "from images" is selected): one per variant.
            (TextBox box, Button browse) MakeImageRow(string caption)
            {
                stack.Children.Add(Caption(caption, 11, new Thickness(18, 2, 0, 2)));
                var grid = new Grid { Margin = new Thickness(18, 0, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = Field(11);
                box.IsReadOnly = true; box.IsEnabled = false;
                Grid.SetColumn(box, 0);
                var browse = new Button { Content = "Browse…", FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
                                          Margin = new Thickness(6, 0, 0, 0), IsEnabled = false, Style = (Style)FindResource("FlatBtn") };
                browse.Click += (_, _) =>
                {
                    var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff" };
                    if (ofd.ShowDialog() == true) box.Text = ofd.FileName;
                };
                Grid.SetColumn(browse, 1);
                grid.Children.Add(box);
                grid.Children.Add(browse);
                stack.Children.Add(grid);
                return (box, browse);
            }
            (_lightImageBox, _lightBrowse) = MakeImageRow("Light-mode picture (seeds the Light variant):");
            (_darkImageBox,  _darkBrowse)  = MakeImageRow("Dark-mode picture (seeds the Dark variant):");

            _bgCheck = new CheckBox
            {
                Content   = "Also use each picture as that variant's window background",
                FontSize  = 11, FontFamily = ThemeFont(), Foreground = B("TextPrimary"),
                Margin    = new Thickness(18, 2, 0, 12),
                IsEnabled = false,
            };
            stack.Children.Add(_bgCheck);

            void SyncImageRow()
            {
                bool img = _fromImage.IsChecked == true;
                _lightImageBox.IsEnabled = img; _lightBrowse.IsEnabled = img;
                _darkImageBox.IsEnabled  = img; _darkBrowse.IsEnabled  = img;
                _bgCheck.IsEnabled       = img;
            }
            _fromCurrent.Checked += (_, _) => SyncImageRow();
            _fromImage.Checked   += (_, _) => SyncImageRow();

            var ok = new Button
            {
                Content = "Create", IsDefault = true, MinWidth = 80,
                Padding = new Thickness(20, 6, 20, 6), Style = (Style)FindResource("SuccessBtn"),
            };
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_nameBox.Text))
                {
                    ThemedMessageDialog.Info(this, "Folder name cannot be empty.", "New theme");
                    return;
                }
                if (FromImage && (!File.Exists(_lightImageBox.Text) || !File.Exists(_darkImageBox.Text)))
                {
                    ThemedMessageDialog.Info(this, "Pick both a light-mode and a dark-mode picture.", "New theme");
                    return;
                }
                Confirmed = true;
                Close();
            };
            var cancel = new Button
            {
                Content = "Cancel", IsCancel = true, MinWidth = 80,
                Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource("FlatBtn"),
            };
            cancel.Click += (_, _) => Close();
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0),
            };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);
            stack.Children.Add(btnRow);

            SetThemedContent("New theme", stack);
            Loaded += (_, _) => _nameBox.Focus();
        }
    }

    // ── Screen eyedropper ──────────────────────────────────────────────────────
    // Freezes the whole virtual desktop into a bitmap and shows it full-screen; hovering
    // reads the pixel under the cursor straight from that frozen bitmap (pixel-exact, and
    // the overlay can't tint what it samples) into a small swatch + hex readout beside the
    // pointer. Left-click captures, right-click / Escape cancels. The window is OPAQUE (it
    // displays the frozen screen) so it reliably receives mouse input — a fully transparent
    // AllowsTransparency overlay is click-through at the OS level and gets no events.
    // Result is reported via the Picked property (NOT DialogResult).
    internal sealed class EyedropperWindow : Window
    {
        public Color? Picked { get; private set; }

        private readonly System.Drawing.Bitmap _shot;
        private readonly int       _originX, _originY;   // physical-px origin of the capture
        private readonly Border    _previewBox;
        private readonly Border    _previewSwatch;
        private readonly TextBlock _previewText;
        private Color _current = Colors.Black;

        public EyedropperWindow()
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;   // physical px
            _originX = vs.Left; _originY = vs.Top;
            _shot = new System.Drawing.Bitmap(vs.Width, vs.Height);
            using (var g = System.Drawing.Graphics.FromImage(_shot))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size);

            WindowStyle           = WindowStyle.None;
            Topmost               = true;
            ShowInTaskbar         = false;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor                = Cursors.Cross;
            Left   = SystemParameters.VirtualScreenLeft;
            Top    = SystemParameters.VirtualScreenTop;
            Width  = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            var canvas = new Canvas();
            canvas.Children.Add(new Image
            {
                Source  = ToBitmapSource(_shot),
                Stretch = Stretch.Fill,
                Width   = SystemParameters.VirtualScreenWidth,
                Height  = SystemParameters.VirtualScreenHeight,
            });

            _previewSwatch = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(3),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            };
            _previewText = new TextBlock
            {
                Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"),
                FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            inner.Children.Add(_previewSwatch);
            inner.Children.Add(_previewText);
            _previewBox = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(225, 28, 28, 28)),
                BorderBrush     = Brushes.Gray, BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4), Padding = new Thickness(7, 5, 9, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                Child = inner,
            };
            canvas.Children.Add(_previewBox);
            Content = canvas;

            MouseMove            += (_, ev) => { PositionPreview(ev.GetPosition(this)); Sample(); };
            MouseLeftButtonDown  += (_, _)  => { Sample(); Picked = _current; Close(); };
            MouseRightButtonDown += (_, _)  => { Picked = null; Close(); };
            KeyDown              += (_, ev) => { if (ev.Key == Key.Escape) { Picked = null; Close(); } };
            Loaded               += (_, _)  => { Focus(); Sample(); };
            Closed               += (_, _)  => _shot.Dispose();
        }

        private void PositionPreview(System.Windows.Point p)
        {
            double x = p.X + 18, y = p.Y + 18;
            if (x + 170 > ActualWidth)  x = p.X - 178;
            if (y + 44  > ActualHeight) y = p.Y - 44;
            Canvas.SetLeft(_previewBox, Math.Max(0, x));
            Canvas.SetTop(_previewBox,  Math.Max(0, y));
        }

        private void Sample()
        {
            if (!GetCursorPos(out var pt)) return;
            int bx = pt.X - _originX, by = pt.Y - _originY;
            if (bx < 0 || by < 0 || bx >= _shot.Width || by >= _shot.Height) return;
            var c = _shot.GetPixel(bx, by);
            _current = Color.FromRgb(c.R, c.G, c.B);
            _previewSwatch.Background = new SolidColorBrush(_current);
            _previewText.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
        {
            IntPtr h = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(h); }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("gdi32.dll")]  [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr hObject);
    }

    // ── Custom HSV colour picker ─────────────────────────────────────────────────
    // A WPF replacement for the WinForms ColorDialog: saturation/value square + hue strip
    // + live preview + editable hex. Styled with the "System (Windows colors)" palette
    // (ThemeManager.ApplySystemTo) and custom chrome so it matches the Theme Builder —
    // and, because that palette is the system one (not the live draft), it stays readable
    // even while a broken draft theme is applied. Result is reported via Selected (NOT
    // DialogResult — that throws on AllowsTransparency windows).
    internal sealed class ColorPickerDialog : ThemedDialog
    {
        public Color? Selected { get; private set; }

        private const double SvW = 210, SvH = 160, HueW = 22;

        private double _hue, _sat, _val;   // 0–360, 0–1, 0–1
        private bool   _updating;
        private bool   _fetchArmed, _isFetching;   // screen eyedropper on leaving the popup

        private readonly Rectangle _baseHue   = new() { Width = SvW, Height = SvH };
        private readonly Ellipse   _svMarker  = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        private readonly Rectangle _hueMarker = new() { Width = HueW, Height = 3, Stroke = Brushes.White, StrokeThickness = 1, Fill = Brushes.Transparent, IsHitTestVisible = false };
        private readonly Border    _preview   = new() { Width = 44, Height = 44, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
        private readonly TextBox   _hexBox    = new() { Width = 96, FontFamily = new FontFamily("Consolas"), FontSize = 13, Padding = new Thickness(5, 3, 5, 3), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _rgbLabel  = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

        public ColorPickerDialog(Color initial)
        {
            FontFamily Font() => ThemeFont();

            (_hue, _sat, _val) = RgbToHsv(initial);

            // ── Saturation / value square ──
            var sv = new Canvas { Width = SvW, Height = SvH, ClipToBounds = true, Cursor = Cursors.Cross };
            var white = new Rectangle
            {
                Width = SvW, Height = SvH,
                Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0),
            };
            var black = new Rectangle
            {
                Width = SvW, Height = SvH,
                Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90),
            };
            sv.Children.Add(_baseHue);
            sv.Children.Add(white);
            sv.Children.Add(black);
            sv.Children.Add(_svMarker);
            sv.MouseLeftButtonDown += (_, ev) => { sv.CaptureMouse(); SetSv(ev.GetPosition(sv)); };
            sv.MouseMove           += (_, ev) => { if (sv.IsMouseCaptured) SetSv(ev.GetPosition(sv)); };
            sv.MouseLeftButtonUp   += (_, _)  => sv.ReleaseMouseCapture();
            var svBorder = new Border { BorderBrush = B("BorderColor"), BorderThickness = new Thickness(1), Child = sv };

            // ── Hue strip ──
            var hue = new Canvas { Width = HueW, Height = SvH, ClipToBounds = true, Cursor = Cursors.SizeNS, Margin = new Thickness(10, 0, 0, 0) };
            var hb = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(0, 1) };
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0),   0.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 0), 1.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 0),   2.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 255), 3.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(0, 0, 255),   4.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 255), 5.0 / 6));
            hb.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0),   6.0 / 6));
            hue.Children.Add(new Rectangle { Width = HueW, Height = SvH, Fill = hb });
            hue.Children.Add(_hueMarker);
            hue.MouseLeftButtonDown += (_, ev) => { hue.CaptureMouse(); SetHue(ev.GetPosition(hue)); };
            hue.MouseMove           += (_, ev) => { if (hue.IsMouseCaptured) SetHue(ev.GetPosition(hue)); };
            hue.MouseLeftButtonUp   += (_, _)  => hue.ReleaseMouseCapture();
            var hueBorder = new Border { BorderBrush = B("BorderColor"), BorderThickness = new Thickness(1), Child = hue };

            var topRow = new StackPanel { Orientation = Orientation.Horizontal };
            topRow.Children.Add(svBorder);
            topRow.Children.Add(hueBorder);

            // ── Preview + hex + rgb ──
            _preview.BorderBrush = B("BorderColor");
            _hexBox.Background  = B("CardBg");
            _hexBox.Foreground  = B("TextPrimary");
            _hexBox.BorderBrush = B("BorderColor");
            _hexBox.CaretBrush  = B("TextPrimary");
            _rgbLabel.Foreground = B("TextMuted");
            _rgbLabel.FontFamily = Font();
            _hexBox.KeyDown += (_, ev) => { if (ev.Key == Key.Enter) ApplyHex(); };
            _hexBox.LostFocus += (_, _) => ApplyHex();
            var midRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            midRow.Children.Add(_preview);
            var hexStack = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            hexStack.Children.Add(new TextBlock { Text = "Hex", FontSize = 11, Foreground = B("TextMuted"), FontFamily = Font() });
            hexStack.Children.Add(_hexBox);
            midRow.Children.Add(hexStack);
            midRow.Children.Add(_rgbLabel);

            // ── OK / Cancel (same styles as the builder footer) ──
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(16, 6, 16, 6),
                                  Style = (Style)FindResource("SuccessBtn") };
            ok.Click += (_, _) => { Selected = HsvToRgb(_hue, _sat, _val); Close(); };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(16, 6, 16, 6),
                                      Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("FlatBtn") };
            cancel.Click += (_, _) => Close();
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            btnRow.Children.Add(ok);
            btnRow.Children.Add(cancel);

            var body = new StackPanel { Margin = new Thickness(16) };
            body.Children.Add(topRow);
            body.Children.Add(midRow);
            body.Children.Add(btnRow);

            SetThemedContent("Pick a colour", body);

            // Move the cursor off the popup → grab a colour from the screen (inkpen).
            MouseEnter += (_, _) => _fetchArmed = true;
            MouseLeave += (_, _) => { if (_fetchArmed && !_isFetching) FetchFromScreen(); };

            UpdateAll();
        }

        /// <summary>Opens the screen eyedropper; a captured pixel becomes the picker's colour.</summary>
        private void FetchFromScreen()
        {
            _isFetching = true;
            try
            {
                var dlg = new EyedropperWindow { Owner = this };
                dlg.ShowDialog();
                if (dlg.Picked is Color c)
                {
                    (_hue, _sat, _val) = RgbToHsv(c);
                    UpdateAll();
                }
            }
            finally
            {
                _isFetching = false;
                _fetchArmed = false;   // require re-entering the popup to re-arm
            }
        }

        private void SetSv(System.Windows.Point p)
        {
            _sat = Math.Clamp(p.X / SvW, 0, 1);
            _val = Math.Clamp(1 - p.Y / SvH, 0, 1);
            UpdateAll();
        }

        private void SetHue(System.Windows.Point p)
        {
            _hue = Math.Clamp(p.Y / SvH, 0, 1) * 360;
            UpdateAll();
        }

        private void ApplyHex()
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(_hexBox.Text.Trim());
                (_hue, _sat, _val) = RgbToHsv(c);
                UpdateAll();
            }
            catch { /* keep previous on invalid input */ }
        }

        private void UpdateAll()
        {
            if (_updating) return;
            _updating = true;
            try
            {
                var c = HsvToRgb(_hue, _sat, _val);
                _baseHue.Fill   = new SolidColorBrush(HsvToRgb(_hue, 1, 1));
                _preview.Background = new SolidColorBrush(c);
                _hexBox.Text    = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                _rgbLabel.Text  = $"R {c.R}   G {c.G}   B {c.B}";
                Canvas.SetLeft(_svMarker, _sat * SvW - 6);
                Canvas.SetTop (_svMarker, (1 - _val) * SvH - 6);
                Canvas.SetTop (_hueMarker, _hue / 360 * SvH - 1);
            }
            finally { _updating = false; }
        }

        // ── HSV ↔ RGB ──
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60.0 % 2) - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if      (h <  60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else              { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255),
                                 (byte)Math.Round((g + m) * 255),
                                 (byte)Math.Round((b + m) * 255));
        }

        private static (double h, double s, double v) RgbToHsv(Color col)
        {
            double r = col.R / 255.0, g = col.G / 255.0, b = col.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            double h = 0;
            if (d != 0)
            {
                if      (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else               h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;
            return (h, max == 0 ? 0 : d / max, max);
        }
    }

    // ── Themed message / confirm dialog ──────────────────────────────────────────
    // Replaces native MessageBox inside the builder so confirmations/errors match the
    // System (Windows colors) palette + chrome (ThemeManager.ApplySystemTo), instead of
    // the always-light native dialog clashing with the dark builder. Uses the system
    // palette (not the live draft), so it stays readable. Result via Confirmed (no
    // DialogResult — that throws on AllowsTransparency windows).
    internal sealed class ThemedMessageDialog : ThemedDialog
    {
        public bool Confirmed { get; private set; }

        private ThemedMessageDialog(string message, string title, bool yesNo)
        {
            var msg = new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
                Foreground = B("TextPrimary"), FontFamily = ThemeFont(), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 16),
            };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (yesNo)
            {
                var yes = new Button { Content = "Yes", MinWidth = 80, Padding = new Thickness(16, 6, 16, 6),
                                       Style = (Style)FindResource("DangerBtn") };
                yes.Click += (_, _) => { Confirmed = true; Close(); };
                var no  = new Button { Content = "No", MinWidth = 80, Padding = new Thickness(16, 6, 16, 6),
                                       Margin = new Thickness(8, 0, 0, 0), Style = (Style)FindResource("FlatBtn"), IsCancel = true };
                no.Click += (_, _) => Close();
                btnRow.Children.Add(yes);
                btnRow.Children.Add(no);
            }
            else
            {
                var ok = new Button { Content = "OK", MinWidth = 80, Padding = new Thickness(16, 6, 16, 6),
                                      Style = (Style)FindResource("FlatBtn"), IsDefault = true, IsCancel = true };
                ok.Click += (_, _) => Close();
                btnRow.Children.Add(ok);
            }

            var body = new StackPanel { Margin = new Thickness(18), MinWidth = 300 };
            body.Children.Add(msg);
            body.Children.Add(btnRow);

            SetThemedContent(title, body);
        }

        /// <summary>Themed Yes/No confirmation. Returns true on Yes.</summary>
        public static bool Confirm(Window owner, string message, string title)
        {
            var d = new ThemedMessageDialog(message, title, yesNo: true) { Owner = owner };
            d.ShowDialog();
            return d.Confirmed;
        }

        /// <summary>Themed single-button (OK) information / error dialog.</summary>
        public static void Info(Window owner, string message, string title)
        {
            var d = new ThemedMessageDialog(message, title, yesNo: false) { Owner = owner };
            d.ShowDialog();
        }
    }
}
