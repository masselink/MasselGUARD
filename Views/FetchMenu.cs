using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>One line of a Fetch menu: a network header, a small section label, a separator, or a pickable item.</summary>
    public sealed record FetchEntry(FetchEntryKind Kind, string Title = "", string Value = "", string? Subtitle = null, bool Emphasis = false)
    {
        public static FetchEntry Header(string title)              => new(FetchEntryKind.Header, title);
        public static FetchEntry Section(string title)             => new(FetchEntryKind.Section, title);
        public static FetchEntry Separator()                       => new(FetchEntryKind.Separator);
        /// <summary>A pickable row. <paramref name="emphasis"/> = bold (used for "all together"); the optional
        /// <paramref name="subtitle"/> (muted, second line) shows what the row would fill in.</summary>
        public static FetchEntry Item(string title, string value, string? subtitle = null, bool emphasis = false)
            => new(FetchEntryKind.Item, title, value, subtitle, emphasis);
    }

    public enum FetchEntryKind { Header, Section, Separator, Item }

    /// <summary>
    /// The themed drop-down used by every "Fetch" button (rule dialog, trusted networks, advanced test). The
    /// default WPF ContextMenu is transparent and ignores the theme, so the text of the window behind it bleeds
    /// through; this one uses the theme's window background, border, text and hover colours.
    /// </summary>
    public static class FetchMenu
    {
        private const string MenuItemStyleXaml =
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>" +
            "  <Setter Property='Foreground' Value='{DynamicResource TextPrimary}'/>" +
            "  <Setter Property='Padding' Value='12,9'/>" +
            "  <Setter Property='Template'>" +
            "    <Setter.Value>" +
            "      <ControlTemplate TargetType='MenuItem'>" +
            "        <Border x:Name='Bd' Background='Transparent' CornerRadius='3' Padding='{TemplateBinding Padding}'>" +
            "          <ContentPresenter ContentSource='Header'/>" +
            "        </Border>" +
            "        <ControlTemplate.Triggers>" +
            "          <Trigger Property='IsHighlighted' Value='True'>" +
            "            <Setter TargetName='Bd' Property='Background' Value='{DynamicResource ListSelected}'/>" +
            "          </Trigger>" +
            "        </ControlTemplate.Triggers>" +
            "      </ControlTemplate>" +
            "    </Setter.Value>" +
            "  </Setter>" +
            "</Style>";

        private static Brush B(string key, Brush fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? fallback;

        /// <summary>The theme's window colour forced fully opaque: some themes use a translucent window brush, and a
        /// popup menu over the dialog must never let the text behind it show through.</summary>
        private static Brush Opaque(string key, Brush fallback)
        {
            if (B(key, fallback) is SolidColorBrush s)
            {
                var c = s.Color; c.A = 255;
                var brush = new SolidColorBrush(c); brush.Freeze();
                return brush;
            }
            return fallback;
        }

        private static FontFamily Font() =>
            Application.Current?.TryFindResource("Theme.FontFamily") as FontFamily ?? new FontFamily("Segoe UI");

        /// <summary>The rows for one network's subnets. With several (an IPv4 and an IPv6 prefix, say) the first,
        /// bold row fills in ALL of them together (its second line shows exactly what that is); under a
        /// "just one subnet" label each single subnet follows, tagged IPv4 / IPv6. With one subnet: one plain row.
        /// <paramref name="store"/> turns a value (or the joined list) into what the pick returns (default: as is).</summary>
        public static IEnumerable<FetchEntry> SubnetEntries(IReadOnlyList<string> values, Func<string, string>? store = null)
        {
            store ??= v => v;
            string Family(string v) => v.Contains(':') ? "IPv6" : "IPv4";
            if (values.Count > 1)
            {
                string all = string.Join(", ", values);
                yield return FetchEntry.Item($"{Lang.T("FetchAllSubnets")} ({values.Count})", store(all), all, emphasis: true);
                yield return FetchEntry.Section(Lang.T("FetchSingleSubnet"));
            }
            foreach (var v in values)
                yield return FetchEntry.Item($"{Family(v)}    {v}", store(v));
        }

        /// <summary>Opens the menu below <paramref name="target"/>; <paramref name="pick"/> receives the chosen item's value.</summary>
        public static void Show(FrameworkElement target, IEnumerable<FetchEntry> entries, Action<string> pick)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement       = PlacementMode.Bottom,
                Background      = Opaque("WindowBg", Brushes.Black),
                BorderBrush     = B("Accent", Brushes.CornflowerBlue),
                BorderThickness = new Thickness(1),
                Foreground      = B("TextPrimary", Brushes.White),
                Padding         = new Thickness(8),
                MinWidth        = Math.Max(360, target.ActualWidth),
            };
            menu.Resources[typeof(MenuItem)] = (Style)XamlReader.Parse(MenuItemStyleXaml);

            foreach (var e in entries)
            {
                switch (e.Kind)
                {
                    case FetchEntryKind.Header:
                        menu.Items.Add(Static(new TextBlock
                        {
                            Text = e.Title, FontFamily = Font(), FontSize = 12, FontWeight = FontWeights.Bold,
                            Foreground = B("Accent", Brushes.CornflowerBlue), Margin = new Thickness(0, 4, 0, 6),
                        }));
                        break;

                    case FetchEntryKind.Section:
                        menu.Items.Add(Static(new TextBlock
                        {
                            Text = e.Title, FontFamily = Font(), FontSize = 10,
                            Foreground = B("TextMuted", Brushes.Gray), Margin = new Thickness(0, 10, 0, 4),
                        }));
                        break;

                    case FetchEntryKind.Separator:
                        menu.Items.Add(Static(new Border
                        {
                            Height = 1, Background = B("BorderColor", Brushes.DimGray), Margin = new Thickness(0, 10, 0, 10),
                        }));
                        break;

                    default:   // Item
                        var head = new StackPanel();
                        head.Children.Add(new TextBlock
                        {
                            Text = e.Title, FontFamily = Font(), FontSize = 12,
                            FontWeight = e.Emphasis ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = B("TextPrimary", Brushes.White), TextTrimming = TextTrimming.CharacterEllipsis,
                        });
                        if (!string.IsNullOrEmpty(e.Subtitle))
                            head.Children.Add(new TextBlock
                            {
                                Text = e.Subtitle, FontFamily = Font(), FontSize = 10, LineHeight = 15,
                                Foreground = B("TextMuted", Brushes.Gray), TextWrapping = TextWrapping.Wrap,
                                MaxWidth = 400, Margin = new Thickness(0, 4, 0, 0),
                            });
                        var item = new MenuItem { Header = head };
                        string value = e.Value;
                        item.Click += (_, _) => pick(value);
                        menu.Items.Add(item);
                        break;
                }
            }
            menu.IsOpen = true;
        }

        /// <summary>A non-interactive row (header / label / separator): no hover, no focus, no click.</summary>
        private static MenuItem Static(UIElement content) =>
            new() { Header = content, IsHitTestVisible = false, Focusable = false, Padding = new Thickness(10, 3, 10, 3) };
    }
}
