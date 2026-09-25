namespace MasselGUARD
{
    /// <summary>
    /// Item in the language picker. Strips any leading emoji/flag prefix from the
    /// display name since WPF cannot render emoji flag sequences.
    /// Loads a flag PNG from lang/flags/{flagCode}.png when available.
    /// </summary>
    public class LangItem
    {
        public string Code { get; }
        public string Name { get; }

        /// <summary>Flag image loaded from lang/flags/{flagCode}.png, or null if not found.</summary>
        public System.Windows.Media.Imaging.BitmapImage? FlagImage { get; }

        public LangItem(string code, string rawName, string flagCode = "")
        {
            Code = code.ToUpperInvariant();

            // Strip leading non-ASCII flag prefix characters (emoji pairs, spaces)
            string trimmed = rawName.TrimStart();
            int i = 0;
            while (i < trimmed.Length && trimmed[i] > 127)
                i++;
            Name = i > 0 && i < trimmed.Length ? trimmed[i..].TrimStart() : trimmed;

            // Load flag PNG from lang/flags/{flagCode}.png
            var fc = string.IsNullOrEmpty(flagCode) ? code.ToLowerInvariant() : flagCode.ToLowerInvariant();
            try
            {
                var exeDir = System.IO.Path.GetDirectoryName(
                    System.Environment.ProcessPath ?? System.AppContext.BaseDirectory) ?? "";
                var path = System.IO.Path.Combine(exeDir, "lang", "flags", fc + ".png");
                if (System.IO.File.Exists(path))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource      = new System.Uri(path, System.UriKind.Absolute);
                    bmp.CacheOption    = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 20;
                    bmp.EndInit();
                    bmp.Freeze();
                    FlagImage = bmp;
                }
            }
            catch { /* flag image is optional - degrade gracefully */ }
        }

        public override string ToString() => $"[{Code}] {Name}";
    }

    /// <summary>Item in the theme ComboBox pickers.</summary>
    public class ThemePickerItem
    {
        public string FolderName  { get; }
        public string DisplayName { get; }

        public ThemePickerItem(string folder, string display)
        {
            FolderName  = folder;
            DisplayName = display;
        }

        public override string ToString() => DisplayName;
    }
}
