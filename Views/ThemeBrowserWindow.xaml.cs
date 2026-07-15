using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Wide pop-up that browses the shared-theme repository: manifest-driven cards with
    /// preview images (dark/light toggle), tag + text filtering, and per-theme install.
    /// </summary>
    public partial class ThemeBrowserWindow : Window
    {
        private readonly MainWindow _main;
        private readonly string     _repoUrl;
        private readonly List<ThemeBrowserItem> _items = new();
        private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
        private ListCollectionView? _view;
        private bool _previewDark = true;
        private bool _shiftPeek;   // hold-Shift Windows-colours fallback active
        private ThemeBrowserItem? _zoomedItem;   // card currently shown enlarged, if any

        /// <summary>True if at least one theme was installed (so the caller refreshes its picker).</summary>
        public bool AnyInstalled { get; private set; }

        public ThemeBrowserWindow(MainWindow main, string repoUrl)
        {
            _main    = main;
            _repoUrl = repoUrl;
            InitializeComponent();
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetCenter("Loading themes…");
            ThemeManifest manifest;
            try { manifest = await ThemeDownloadService.FetchManifestAsync(_repoUrl); }
            catch (Exception ex) { SetCenter($"Could not load themes.\n\n{ex.Message}"); return; }

            _items.Clear();
            var sharedRoot        = ThemeManager.SharedThemeRoot;
            var installedVersions = ThemeDownloadService.LoadInstalledVersions(sharedRoot);
            foreach (var e in manifest.Themes)
            {
                if (string.IsNullOrWhiteSpace(e.Id)) continue;
                bool isInstalled = Directory.Exists(Path.Combine(sharedRoot, e.Id));
                bool updateAvailable = isInstalled && !string.IsNullOrWhiteSpace(e.Version) &&
                    installedVersions.TryGetValue(e.Id, out var installedVer) &&
                    string.CompareOrdinal(e.Version, installedVer) > 0;
                _items.Add(new ThemeBrowserItem(e, manifest.RawBase)
                {
                    IsInstalled      = isInstalled,
                    UpdateAvailable  = updateAvailable,
                });
            }

            if (_items.Count == 0) { SetCenter("No themes found in this repository."); return; }

            BuildTagChips();
            _view = new ListCollectionView(_items) { Filter = FilterItem };
            ItemsHost.ItemsSource = _view;
            CenterStatus.Visibility = Visibility.Collapsed;
            StatusText.Text = $"{_items.Count} theme(s) available";

            await LoadPreviewsAsync();
        }

        // ── Previews ──────────────────────────────────────────────────────────
        private async Task LoadPreviewsAsync()
        {
            foreach (var it in _items)
            {
                await EnsurePreview(it, _previewDark);
                it.Preview = _previewDark ? it.DarkBmp : it.LightBmp;
            }
        }

        /// <summary>Lazily fetches the dark/light preview (cached); falls back to the other variant's path.</summary>
        private static async Task EnsurePreview(ThemeBrowserItem it, bool dark)
        {
            if (dark ? it.DarkTried : it.LightTried) return;
            if (dark) it.DarkTried = true; else it.LightTried = true;

            var primary  = dark ? it.Entry.PreviewDark  : it.Entry.PreviewLight;
            var fallback = dark ? it.Entry.PreviewLight : it.Entry.PreviewDark;
            var rel      = !string.IsNullOrWhiteSpace(primary) ? primary : fallback;

            var data = await ThemeDownloadService.FetchRawAsync(it.RawBase, ResolvePreviewPath(it.Entry.Path, rel));
            var bmp  = BmpFromBytes(data);
            if (dark) it.DarkBmp = bmp; else it.LightBmp = bmp;
        }

        /// <summary>
        /// previewDark/previewLight are relative to the theme's own <c>path</c> folder (a leading
        /// "/" is accepted and ignored, per SHARED_THEMES_REPO_GUIDE.md) — they no longer repeat
        /// the "themes/&lt;id&gt;/" prefix, so it has to be re-added here before fetching.
        /// </summary>
        private static string ResolvePreviewPath(string themePath, string previewRelative)
        {
            if (string.IsNullOrWhiteSpace(previewRelative)) return "";
            var path    = themePath.Trim('/');
            var preview = previewRelative.TrimStart('/');
            return string.IsNullOrEmpty(path) ? preview : $"{path}/{preview}";
        }

        private async void PreviewVariant_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            _previewDark = PreviewDarkBtn.IsChecked == true;
            await LoadPreviewsAsync();
            if (_zoomedItem != null) ZoomImage.Source = _zoomedItem.Preview;   // keep the zoomed image in sync
        }

        // ── Zoomed preview ───────────────────────────────────────────────────
        private void Preview_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ThemeBrowserItem item) return;
            if (item.Preview == null) return;
            _zoomedItem = item;
            ZoomImage.Source    = item.Preview;
            ZoomOverlay.Visibility = Visibility.Visible;
        }

        private void CloseZoom_Click(object sender, RoutedEventArgs e)
        {
            ZoomOverlay.Visibility = Visibility.Collapsed;
            ZoomImage.Source       = null;
            _zoomedItem            = null;
        }

        private static BitmapImage? BmpFromBytes(byte[]? data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // ── Filtering ─────────────────────────────────────────────────────────
        private void BuildTagChips()
        {
            TagPanel.Children.Clear();
            var tags = _items.SelectMany(i => i.Tags)
                             .Where(t => !string.IsNullOrWhiteSpace(t))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(t => t, StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                var chip = new ToggleButton
                {
                    Content = tag,
                    Tag     = tag,
                    Style   = (Style)FindResource("TagChip"),
                };
                chip.Checked   += Tag_Toggled;
                chip.Unchecked += Tag_Toggled;
                TagPanel.Children.Add(chip);
            }
        }

        private void Tag_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton tb || tb.Tag is not string tag) return;
            if (tb.IsChecked == true) _selectedTags.Add(tag); else _selectedTags.Remove(tag);
            _view?.Refresh();
        }

        private void Filter_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

        private bool FilterItem(object o)
        {
            if (o is not ThemeBrowserItem it) return false;

            // Selected tags narrow the list (AND).
            foreach (var t in _selectedTags)
                if (!it.Tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    return false;

            var q = SearchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                bool hit = it.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || it.Entry.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || it.Tags.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (!hit) return false;
            }
            return true;
        }

        // ── Install ───────────────────────────────────────────────────────────
        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not ThemeBrowserItem it) return;
            if (!it.CanInstall) return;

            if (it.IsInstalled)
            {
                var message = it.UpdateAvailable
                    ? $"A newer version of '{it.Name}' is available. Updating will overwrite your local copy — " +
                      "any changes you made to it will be lost.\n\nContinue?"
                    : $"'{it.Name}' is already installed. Redownloading will overwrite your local copy — " +
                      "any changes you made to it will be lost.\n\nContinue?";
                var title = it.UpdateAvailable ? "Update theme" : "Redownload theme";
                if (!ThemedMessageDialog.Confirm(this, message, title)) return;
            }

            it.Busy = true;
            StatusText.Text = $"Installing {it.Name}…";
            try
            {
                var n = await ThemeDownloadService.InstallThemeAsync(it.RawBase, it.Entry, ThemeManager.SharedThemeRoot);
                it.IsInstalled     = true;
                it.UpdateAvailable = false;   // just installed the latest version
                AnyInstalled       = true;
                StatusText.Text = $"Installed {it.Name} ({n} file(s)).";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Install failed: {ex.Message}";
            }
            finally { it.Busy = false; }
        }

        // ── Chrome ────────────────────────────────────────────────────────────
        private void SetCenter(string text)
        {
            CenterStatus.Text       = text;
            CenterStatus.Visibility = Visibility.Visible;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ── Hold-Shift Windows-colours fallback ────────────────────────────────
        // Same escape hatch as the Theme Manager: lets the user confirm a card's preview
        // is readable even if the currently-active theme (e.g. a broken live edit) isn't.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && ZoomOverlay.Visibility == Visibility.Visible)
            {
                CloseZoom_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }

            bool inTextBox = Keyboard.FocusedElement is TextBox;
            if ((e.Key is Key.LeftShift or Key.RightShift) && !_shiftPeek && !inTextBox)
            {
                _shiftPeek = true;
                ThemeManager.ApplySystemTo(Resources);   // local resources shadow the app theme
            }
            base.OnPreviewKeyDown(e);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if ((e.Key is Key.LeftShift or Key.RightShift) && _shiftPeek)
            {
                _shiftPeek = false;
                Resources.Clear();   // drop the local override → back to the active theme
            }
            base.OnPreviewKeyUp(e);
        }

        /// <summary>Clear the hold-Shift fallback if focus leaves mid-hold (KeyUp may not fire).</summary>
        protected override void OnDeactivated(EventArgs e)
        {
            if (_shiftPeek) { _shiftPeek = false; Resources.Clear(); }
            base.OnDeactivated(e);
        }
    }

    /// <summary>Card view-model for one repository theme.</summary>
    public sealed class ThemeBrowserItem : INotifyPropertyChanged
    {
        public ThemeManifestEntry Entry   { get; }
        public string            RawBase { get; }

        public ThemeBrowserItem(ThemeManifestEntry entry, string rawBase)
        {
            Entry = entry; RawBase = rawBase;
        }

        public string Name        => Entry.Name;
        public string Author      => string.IsNullOrWhiteSpace(Entry.Author) ? "" : "by " + Entry.Author;
        public string Description => Entry.Description;
        public IReadOnlyList<string> Tags => Entry.Tags;
        public string TagsDisplay => Entry.Tags.Count > 0 ? string.Join("  ·  ", Entry.Tags) : "";

        // Preview cache (per variant) + "already tried to fetch" flags.
        public BitmapImage? DarkBmp, LightBmp;
        public bool DarkTried, LightTried;

        private BitmapImage? _preview;
        public BitmapImage? Preview
        {
            get => _preview;
            set { _preview = value; OnPC(nameof(Preview)); }
        }

        private bool _installed;
        public bool IsInstalled
        {
            get => _installed;
            set { _installed = value; OnPC(nameof(IsInstalled)); OnPC(nameof(CanInstall)); OnPC(nameof(InstallLabel)); }
        }

        /// <summary>True when this theme is installed and the manifest's "version" is newer
        /// than what was recorded at install time — see ThemeDownloadService.CheckForThemeUpdatesAsync.</summary>
        private bool _updateAvailable;
        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set { _updateAvailable = value; OnPC(nameof(UpdateAvailable)); OnPC(nameof(InstallLabel)); }
        }

        private bool _busy;
        public bool Busy
        {
            get => _busy;
            set { _busy = value; OnPC(nameof(CanInstall)); OnPC(nameof(InstallLabel)); }
        }

        public bool   CanInstall  => !_busy;
        public string InstallLabel => _busy ? "Installing…"
            : !_installed        ? "Install"
            : _updateAvailable   ? "Update"
            : "Reinstall";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
