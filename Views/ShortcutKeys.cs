using System;
using System.Windows.Input;
using MasselGUARD.Models;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Maps the text-based <see cref="Shortcut"/> to WPF keys and to the Win32 values a system-wide hotkey needs, and
    /// back (for the Settings box that records a key press). GUI-only; the parsing itself is the pure <see cref="Shortcut"/>.
    /// </summary>
    public static class ShortcutKeys
    {
        public const int ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;

        /// <summary>The WPF key for a shortcut's key name ("B", "7", "F9", "PageUp"), or null.</summary>
        public static Key? ToKey(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.Length == 1)
            {
                char c = name[0];
                if (c is >= 'A' and <= 'Z') return (Key)((int)Key.A + (c - 'A'));
                if (c is >= '0' and <= '9') return (Key)((int)Key.D0 + (c - '0'));
                return null;
            }
            if (name[0] == 'F' && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
                return (Key)((int)Key.F1 + (n - 1));
            return name switch
            {
                "Space" => Key.Space, "Enter" => Key.Enter, "Tab" => Key.Tab, "Insert" => Key.Insert, "Delete" => Key.Delete,
                "Home" => Key.Home, "End" => Key.End, "PageUp" => Key.PageUp, "PageDown" => Key.PageDown,
                "Up" => Key.Up, "Down" => Key.Down, "Left" => Key.Left, "Right" => Key.Right,
                _ => null,
            };
        }

        /// <summary>The shortcut key name for a WPF key, or null when the key is not a usable shortcut key
        /// (modifiers on their own, punctuation, Escape ...).</summary>
        public static string? ToName(Key key)
        {
            if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
            if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
            if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
            if (key is >= Key.F1 and <= Key.F24) return "F" + (1 + (key - Key.F1));
            return key switch
            {
                Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Insert => "Insert", Key.Delete => "Delete",
                Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.Next => "PageDown",
                Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
                _ => null,
            };
        }

        /// <summary>The Win32 MOD_* flags of a shortcut.</summary>
        public static int ToModifiers(Shortcut s) =>
            (s.Ctrl ? ModControl : 0) | (s.Alt ? ModAlt : 0) | (s.Shift ? ModShift : 0) | (s.Win ? ModWin : 0);

        /// <summary>The Win32 virtual-key code of a shortcut's key, or 0.</summary>
        public static int ToVirtualKey(Shortcut s) => ToKey(s.Key) is Key k ? KeyInterop.VirtualKeyFromKey(k) : 0;

        /// <summary>Does this key event (with the current modifiers) match the shortcut?</summary>
        public static bool Matches(Shortcut s, KeyEventArgs e)
        {
            if (s.IsEmpty || ToKey(s.Key) is not Key want) return false;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt combinations arrive as Key.System
            if (key != want) return false;
            var m = Keyboard.Modifiers;
            return ((m & ModifierKeys.Control) != 0) == s.Ctrl
                && ((m & ModifierKeys.Alt)     != 0) == s.Alt
                && ((m & ModifierKeys.Shift)   != 0) == s.Shift
                && ((m & ModifierKeys.Windows) != 0) == s.Win;
        }

        /// <summary>Builds the shortcut text for a key press (the Settings box that records a shortcut); null when the
        /// press is not a valid shortcut (only modifiers, no Ctrl/Alt/Win, an unsupported key).</summary>
        public static string? FromKeyPress(KeyEventArgs e)
        {
            var key  = e.Key == Key.System ? e.SystemKey : e.Key;
            var name = ToName(key);
            if (name == null) return null;
            var m = Keyboard.Modifiers;
            var s = new Shortcut((m & ModifierKeys.Control) != 0, (m & ModifierKeys.Alt) != 0,
                                 (m & ModifierKeys.Shift) != 0, (m & ModifierKeys.Windows) != 0, name);
            var text = s.ToString();
            return Shortcut.TryParse(text, out var parsed) && !parsed.IsEmpty ? text : null;
        }
    }
}
