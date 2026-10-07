using System;
using System.Collections.Generic;
using System.Linq;

namespace MasselGUARD.Models
{
    /// <summary>
    /// A keyboard shortcut kept as text in <c>config.json</c> (for example <c>Ctrl+Shift+B</c>). This pure type only
    /// parses, validates and normalises the text; the GUI maps it to WPF keys and, for a system-wide hotkey, to a
    /// Win32 virtual key. WPF-free and CLI-shared; <see cref="RunSelfTest"/> is part of <c>MasselGUARDcli selftest</c>.
    /// <para>A shortcut needs at least one of Ctrl, Alt or Win (Shift alone would hijack normal typing) and exactly
    /// one key: a letter, a digit, F1-F24 or one of the navigation keys listed in <see cref="NamedKeys"/>.</para>
    /// </summary>
    public readonly record struct Shortcut(bool Ctrl, bool Alt, bool Shift, bool Win, string Key)
    {
        /// <summary>The non-letter, non-digit keys that are accepted (canonical spelling).</summary>
        public static readonly string[] NamedKeys =
        {
            "Space", "Enter", "Tab", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
        };

        /// <summary>The default shortcut of the quick DNS bypass.</summary>
        public const string DefaultText = "Ctrl+Alt+D";

        public bool IsEmpty => string.IsNullOrEmpty(Key);

        /// <summary>Canonical text: modifiers in the fixed order Ctrl, Alt, Shift, Win, then the key (<c>Ctrl+Alt+F9</c>).</summary>
        public override string ToString()
        {
            if (IsEmpty) return "";
            var parts = new List<string>();
            if (Ctrl)  parts.Add("Ctrl");
            if (Alt)   parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win)   parts.Add("Win");
            parts.Add(Key);
            return string.Join("+", parts);
        }

        /// <summary>Parses user text. Accepts any case, <c>Control</c>/<c>Ctl</c>, <c>Windows</c>/<c>Meta</c>, spaces around
        /// the plus signs and any modifier order. Returns false (and <see cref="Shortcut"/> default) when the text is not a
        /// usable shortcut. An empty or whitespace string is "no shortcut": it returns true with an empty value.</summary>
        public static bool TryParse(string? text, out Shortcut result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(text)) return true;   // "disabled"

            bool ctrl = false, alt = false, shift = false, win = false;
            string? key = null;
            foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries))
            {
                if (raw.Length == 0) return false;   // "Ctrl++B", leading or trailing "+"
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": case "ctl": ctrl = true; break;
                    case "alt":                              alt = true; break;
                    case "shift":                            shift = true; break;
                    case "win": case "windows": case "meta": case "super": win = true; break;
                    default:
                        if (key != null) return false;       // two keys
                        key = CanonicalKey(raw);
                        if (key == null) return false;
                        break;
                }
            }
            if (key == null) return false;                    // modifiers only
            if (!ctrl && !alt && !win) return false;          // Shift alone (or nothing) would break typing
            result = new Shortcut(ctrl, alt, shift, win, key);
            return true;
        }

        /// <summary>The configured text when valid, else <see cref="DefaultText"/>.</summary>
        public static Shortcut ParseOrDefault(string? text)
        {
            if (TryParse(text, out var s)) return s;
            TryParse(DefaultText, out var d);
            return d;
        }

        /// <summary>Canonical spelling of a key name, or null when it is not accepted.</summary>
        public static string? CanonicalKey(string name)
        {
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c.ToString();
                return null;
            }
            if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
                return "F" + n;
            return NamedKeys.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else fails.Add(name); }
            void Eq(string name, string got, string want) { if (got == want) pass++; else fails.Add($"{name}: got '{got}' want '{want}'"); }
            string Norm(string? s) => TryParse(s, out var r) ? r.ToString() : "<invalid>";

            Eq("default",            Norm(DefaultText), "Ctrl+Alt+D");
            Eq("lowercase",          Norm("ctrl+shift+b"), "Ctrl+Shift+B");
            Eq("order-normalised",   Norm("Shift+Ctrl+B"), "Ctrl+Shift+B");
            Eq("spaces",             Norm(" Ctrl + Alt + F9 "), "Ctrl+Alt+F9");
            Eq("control-alias",      Norm("Control+K"), "Ctrl+K");
            Eq("windows-alias",      Norm("Windows+Alt+D"), "Alt+Win+D");
            Eq("digit",              Norm("Ctrl+1"), "Ctrl+1");
            Eq("function-key",       Norm("alt+f12"), "Alt+F12");
            Eq("f24",                Norm("Ctrl+F24"), "Ctrl+F24");
            Eq("named-key",          Norm("ctrl+pageup"), "Ctrl+PageUp");
            Eq("space",              Norm("Ctrl+Alt+Space"), "Ctrl+Alt+Space");
            Eq("win-only-modifier",  Norm("Win+B"), "Win+B");

            Eq("empty-is-disabled",  Norm(""), "");
            Eq("whitespace-disabled", Norm("   "), "");
            Eq("null-disabled",      Norm(null), "");
            Check("empty-is-valid-and-empty", TryParse("", out var e) && e.IsEmpty);

            Eq("shift-only-rejected", Norm("Shift+B"), "<invalid>");
            Eq("no-modifier-rejected", Norm("B"), "<invalid>");
            Eq("modifiers-only",     Norm("Ctrl+Shift"), "<invalid>");
            Eq("two-keys",           Norm("Ctrl+A+B"), "<invalid>");
            Eq("double-plus",        Norm("Ctrl++B"), "<invalid>");
            Eq("trailing-plus",      Norm("Ctrl+B+"), "<invalid>");
            Eq("unknown-key",        Norm("Ctrl+Banana"), "<invalid>");
            Eq("f25-rejected",       Norm("Ctrl+F25"), "<invalid>");
            Eq("f0-rejected",        Norm("Ctrl+F0"), "<invalid>");
            Eq("punctuation",        Norm("Ctrl+;"), "<invalid>");

            Check("parseordefault-valid",   ParseOrDefault("Alt+X").ToString() == "Alt+X");
            Check("parseordefault-invalid", ParseOrDefault("nonsense").ToString() == DefaultText);
            Check("parseordefault-empty",   ParseOrDefault("").IsEmpty);
            Check("roundtrip",              TryParse(new Shortcut(true, true, false, true, "K").ToString(), out var rt) && rt == new Shortcut(true, true, false, true, "K"));
            Check("equality-ignores-order", TryParse("Shift+Ctrl+B", out var a) && TryParse("Ctrl+Shift+B", out var b) && a == b);
            return (pass, fails.Count, fails);
        }
    }
}
