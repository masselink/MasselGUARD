using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MasselGUARD.Models;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Renders activity-log entries in two columns (Time | Event) for both the main-window log and
    /// the pop-out <see cref="LogWindow"/>. Each entry is one paragraph with a hanging indent: the
    /// timestamp sits in a fixed-width slot and the message starts at <c>timeColWidth</c>, so wrapped
    /// lines stay under the Event column and proportional-font timestamps can't push events out of
    /// line. (A FlowDocument <c>Table</c> was tried first - inside a RichTextBox it squeezed the
    /// star-sized Event column to one character.)
    /// </summary>
    internal static class LogTable
    {
        /// <summary>Build one log line. Continuation entries belong to the entry above, so their time
        /// slot is left empty and the text is marked with ↳.</summary>
        public static Paragraph BuildRow(LogEntry entry, Brush tsBrush, Brush msgBrush, double timeColWidth)
        {
            var time = new TextBlock
            {
                Text       = entry.IsContinuation ? "" : entry.Timestamp.ToString("HH:mm:ss"),
                Width      = timeColWidth,
                Foreground = tsBrush,
            };
            var p = new Paragraph
            {
                Margin     = new Thickness(timeColWidth, 0, 0, 0),
                TextIndent = -timeColWidth,   // first line starts at 0 with the time slot
            };
            // The slot is a UI element, so it doesn't pick up the log's theme font on its own -
            // follow the paragraph's (inherited) font so time and event text match.
            time.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(Paragraph.FontFamily)) { Source = p });
            time.SetBinding(TextBlock.FontSizeProperty,   new System.Windows.Data.Binding(nameof(Paragraph.FontSize))   { Source = p });
            p.Inlines.Add(new InlineUIContainer(time) { BaselineAlignment = BaselineAlignment.TextBottom });
            p.Inlines.Add(new Run((entry.IsContinuation ? "↳ " : "") + entry.Message) { Foreground = msgBrush });
            return p;
        }

        /// <summary>Width of the Time slot for <paramref name="host"/>'s current font: a full "00:00:00"
        /// timestamp plus a small gap before the event text. The header's Time column uses the same
        /// value, so it follows theme / font-size changes instead of a fixed pixel guess.</summary>
        public static double TimeWidth(Control host)
        {
            var ft = new FormattedText("00:00:00", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(host.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                host.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(host).PixelsPerDip);
            return System.Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 10;
        }

        /// <summary>Insert a line at the top (the log shows newest first).</summary>
        public static void Prepend(FlowDocument doc, Paragraph row)
        {
            doc.PagePadding = new Thickness(0);   // start at the RichTextBox padding, like the header
            if (doc.Blocks.FirstBlock is { } first) doc.Blocks.InsertBefore(first, row);
            else                                    doc.Blocks.Add(row);
        }

        /// <summary>Replace the log with <paramref name="rowsNewestFirst"/> in one batched edit. Adding a
        /// few hundred lines one change at a time to a RichTextBox froze startup; BeginChange/EndChange
        /// makes it a single change.</summary>
        public static void Fill(RichTextBox box, IEnumerable<Paragraph> rowsNewestFirst)
        {
            box.BeginChange();
            try
            {
                var doc = box.Document;
                doc.Blocks.Clear();
                doc.PagePadding = new Thickness(0);
                foreach (var r in rowsNewestFirst) doc.Blocks.Add(r);
            }
            finally { box.EndChange(); }
        }
    }
}
