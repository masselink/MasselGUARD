using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WGClientWifiSwitcher
{
    /// <summary>
    /// Static localisation manager.
    ///
    /// Language files are JSON files placed next to the executable in a "lang" subfolder.
    /// Each file must contain a "_code" key (e.g. "en", "nl") and a "_language" key
    /// for the display name shown in the language picker.
    ///
    /// To add a new language: copy lang/en.json, rename it to your language code
    /// (e.g. lang/de.json), translate every value, and restart the app.
    ///
    /// Keys are accessed via Lang.Instance["KeyName"] or the typed helper
    /// Lang.T("KeyName"). Use Lang.T("KeyName", arg1, arg2) for format strings.
    ///
    /// The singleton implements INotifyPropertyChanged so WPF bindings such as
    /// {Binding [KeyName], Source={x:Static local:Lang.Instance}} update live.
    /// </summary>
    public sealed class Lang : INotifyPropertyChanged
    {
        // ── Singleton ───────────────────────────────────────────────────────
        public static Lang Instance { get; } = new Lang();
        private Lang() { Load("en"); }

        // ── State ────────────────────────────────────────────────────────────
        private Dictionary<string, string> _strings = new();
        private string _currentCode = "en";

        public string CurrentCode     => _currentCode;
        public string CurrentLanguage => _strings.TryGetValue("_language", out var l) ? l : _currentCode;

        // ── Load ─────────────────────────────────────────────────────────────
        /// <summary>Load a language by its code (e.g. "en", "nl").</summary>
        public void Load(string code)
        {
            var path = LangFilePath(code);
            if (!File.Exists(path))
            {
                // Fall back to English if the requested file doesn't exist
                path = LangFilePath("en");
                if (!File.Exists(path)) return;
                code = "en";
            }

            try
            {
                var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                           ?? new Dictionary<string, string>();
                _strings     = dict;
                _currentCode = code;

                // Save to config so the choice persists across restarts
                AppConfig.SaveLanguage(code);

                // Notify all bindings that every key may have changed
                OnPropertyChanged(string.Empty);
                LanguageChanged?.Invoke(this, EventArgs.Empty);
            }
            catch { /* corrupt file — keep current strings */ }
        }

        /// <summary>Returns all available language codes found in the lang folder.</summary>
        public static List<(string code, string name)> AvailableLanguages()
        {
            var dir = LangDir();
            if (!Directory.Exists(dir)) return new List<(string, string)> { ("en", "English") };

            var result = new List<(string code, string name)>();
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                try
                {
                    var json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict == null) continue;
                    var code = dict.TryGetValue("_code",     out var c) ? c : Path.GetFileNameWithoutExtension(file);
                    var name = dict.TryGetValue("_language", out var n) ? n : code;
                    result.Add((code, name));
                }
                catch { }
            }
            return result.Count > 0 ? result : new List<(string, string)> { ("en", "English") };
        }

        // ── Indexer — used by WPF bindings ───────────────────────────────────
        public string this[string key] =>
            _strings.TryGetValue(key, out var v) ? v : $"[{key}]";

        // ── Helpers ──────────────────────────────────────────────────────────
        /// <summary>Translate a key, optionally formatting with arguments.</summary>
        public static string T(string key, params object[] args)
        {
            var s = Instance[key];
            if (args.Length == 0) return s;
            try { return string.Format(s, args); }
            catch { return s; }
        }

        // ── Paths ────────────────────────────────────────────────────────────
        private static string LangDir()
        {
            // Prefer lang/ next to the exe; fall back to next to the .dll (debug)
            var exeDir = Path.GetDirectoryName(
                Environment.ProcessPath
                ?? AppContext.BaseDirectory)!;
            return Path.Combine(exeDir, "lang");
        }

        private static string LangFilePath(string code) =>
            Path.Combine(LangDir(), code + ".json");

        // ── INotifyPropertyChanged ───────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler?                LanguageChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
