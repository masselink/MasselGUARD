using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace MasselGUARD
{
    /// <summary>
    /// Minimal SVG path-data ("d" attribute) → <see cref="GraphicsPath"/> converter, used to render
    /// a theme-supplied DNS badge silhouette with GDI+. Supports M/L/H/V/C/S/Q/T/A/Z (absolute and
    /// relative). Arcs are converted to cubic béziers. Returns null on empty/invalid input so callers
    /// can fall back to a built-in default. No external dependency.
    /// </summary>
    internal static class SvgPathParser
    {
        public static GraphicsPath? Parse(string? d)
        {
            if (string.IsNullOrWhiteSpace(d)) return null;
            try
            {
                var toks = Tokenize(d);
                if (toks.Count == 0) return null;

                var path = new GraphicsPath();
                float cx = 0, cy = 0, sx = 0, sy = 0;   // current point, subpath start
                float lastCx = 0, lastCy = 0; bool haveCubic = false;   // last cubic ctrl (for S)
                float lastQx = 0, lastQy = 0; bool haveQuad = false;    // last quad ctrl (for T)
                char cmd = '\0';
                int i = 0;

                float N() => toks[i++].num;

                while (i < toks.Count)
                {
                    if (toks[i].isCmd) { cmd = toks[i].cmd; i++; }
                    bool rel = char.IsLower(cmd);
                    switch (char.ToUpperInvariant(cmd))
                    {
                        case 'M':
                        {
                            float x = N(), y = N(); if (rel) { x += cx; y += cy; }
                            cx = sx = x; cy = sy = y;
                            path.StartFigure();
                            cmd = rel ? 'l' : 'L';   // subsequent pairs are implicit line-to
                            haveCubic = haveQuad = false;
                            break;
                        }
                        case 'L':
                        {
                            float x = N(), y = N(); if (rel) { x += cx; y += cy; }
                            path.AddLine(cx, cy, x, y); cx = x; cy = y;
                            haveCubic = haveQuad = false;
                            break;
                        }
                        case 'H':
                        {
                            float x = N(); if (rel) x += cx;
                            path.AddLine(cx, cy, x, cy); cx = x;
                            haveCubic = haveQuad = false;
                            break;
                        }
                        case 'V':
                        {
                            float y = N(); if (rel) y += cy;
                            path.AddLine(cx, cy, cx, y); cy = y;
                            haveCubic = haveQuad = false;
                            break;
                        }
                        case 'C':
                        {
                            float x1 = N(), y1 = N(), x2 = N(), y2 = N(), x = N(), y = N();
                            if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; }
                            path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                            lastCx = x2; lastCy = y2; haveCubic = true; haveQuad = false; cx = x; cy = y;
                            break;
                        }
                        case 'S':
                        {
                            float x2 = N(), y2 = N(), x = N(), y = N();
                            if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; }
                            float x1 = haveCubic ? 2 * cx - lastCx : cx;
                            float y1 = haveCubic ? 2 * cy - lastCy : cy;
                            path.AddBezier(cx, cy, x1, y1, x2, y2, x, y);
                            lastCx = x2; lastCy = y2; haveCubic = true; haveQuad = false; cx = x; cy = y;
                            break;
                        }
                        case 'Q':
                        {
                            float qx = N(), qy = N(), x = N(), y = N();
                            if (rel) { qx += cx; qy += cy; x += cx; y += cy; }
                            AddQuad(path, cx, cy, qx, qy, x, y);
                            lastQx = qx; lastQy = qy; haveQuad = true; haveCubic = false; cx = x; cy = y;
                            break;
                        }
                        case 'T':
                        {
                            float x = N(), y = N(); if (rel) { x += cx; y += cy; }
                            float qx = haveQuad ? 2 * cx - lastQx : cx;
                            float qy = haveQuad ? 2 * cy - lastQy : cy;
                            AddQuad(path, cx, cy, qx, qy, x, y);
                            lastQx = qx; lastQy = qy; haveQuad = true; haveCubic = false; cx = x; cy = y;
                            break;
                        }
                        case 'A':
                        {
                            float rx = N(), ry = N(), rot = N(), laf = N(), sf = N(), x = N(), y = N();
                            if (rel) { x += cx; y += cy; }
                            AddArc(path, cx, cy, rx, ry, rot, laf != 0, sf != 0, x, y);
                            cx = x; cy = y; haveCubic = haveQuad = false;
                            break;
                        }
                        case 'Z':
                        {
                            path.CloseFigure(); cx = sx; cy = sy;
                            haveCubic = haveQuad = false;
                            break;
                        }
                        default:
                            return null;   // unknown command → bail, use default
                    }
                }
                return path;
            }
            catch { return null; }
        }

        private static void AddQuad(GraphicsPath p, float x0, float y0, float qx, float qy, float x, float y)
        {
            // Quadratic → cubic bézier.
            float c1x = x0 + 2f / 3f * (qx - x0), c1y = y0 + 2f / 3f * (qy - y0);
            float c2x = x  + 2f / 3f * (qx - x),  c2y = y  + 2f / 3f * (qy - y);
            p.AddBezier(x0, y0, c1x, c1y, c2x, c2y, x, y);
        }

        private static void AddArc(GraphicsPath path, float x1, float y1, float rx, float ry,
                                   float phiDeg, bool largeArc, bool sweep, float x2, float y2)
        {
            if (rx == 0 || ry == 0) { path.AddLine(x1, y1, x2, y2); return; }
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double phi = phiDeg * Math.PI / 180.0;
            double cosP = Math.Cos(phi), sinP = Math.Sin(phi);
            double dx = (x1 - x2) / 2.0, dy = (y1 - y2) / 2.0;
            double x1p =  cosP * dx + sinP * dy;
            double y1p = -sinP * dx + cosP * dy;
            double rx2 = rx * rx, ry2 = ry * ry, x1p2 = x1p * x1p, y1p2 = y1p * y1p;
            double lambda = x1p2 / rx2 + y1p2 / ry2;
            if (lambda > 1) { double s = Math.Sqrt(lambda); rx = (float)(rx * s); ry = (float)(ry * s); rx2 = rx * rx; ry2 = ry * ry; }
            double sign = (largeArc != sweep) ? 1 : -1;
            double num = rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2;
            if (num < 0) num = 0;
            double co = sign * Math.Sqrt(num / (rx2 * y1p2 + ry2 * x1p2));
            double cxp =  co * rx * y1p / ry;
            double cyp = -co * ry * x1p / rx;
            double cx = cosP * cxp - sinP * cyp + (x1 + x2) / 2.0;
            double cy = sinP * cxp + cosP * cyp + (y1 + y2) / 2.0;
            double th1 = AngleBetween(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double dth = AngleBetween((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && dth > 0) dth -= 2 * Math.PI;
            else if (sweep && dth < 0) dth += 2 * Math.PI;

            int segs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2)));
            double delta = dth / segs;
            double t = 8.0 / 3.0 * Math.Sin(delta / 4) * Math.Sin(delta / 4) / Math.Sin(delta / 2);
            double px = x1, py = y1;
            for (int s = 0; s < segs; s++)
            {
                double a1 = th1 + s * delta, a2 = th1 + (s + 1) * delta;
                double cosA1 = Math.Cos(a1), sinA1 = Math.Sin(a1), cosA2 = Math.Cos(a2), sinA2 = Math.Sin(a2);
                double ex = cosP * rx * cosA2 - sinP * ry * sinA2 + cx;
                double ey = sinP * rx * cosA2 + cosP * ry * sinA2 + cy;
                double d1x = -rx * cosP * sinA1 - ry * sinP * cosA1;
                double d1y = -rx * sinP * sinA1 + ry * cosP * cosA1;
                double d2x = -rx * cosP * sinA2 - ry * sinP * cosA2;
                double d2y = -rx * sinP * sinA2 + ry * cosP * cosA2;
                path.AddBezier((float)px, (float)py,
                               (float)(px + t * d1x), (float)(py + t * d1y),
                               (float)(ex - t * d2x), (float)(ey - t * d2y),
                               (float)ex, (float)ey);
                px = ex; py = ey;
            }
        }

        private static double AngleBetween(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            double a = Math.Acos(Math.Max(-1, Math.Min(1, len == 0 ? 1 : dot / len)));
            return (ux * vy - uy * vx < 0) ? -a : a;
        }

        private static List<(bool isCmd, char cmd, float num)> Tokenize(string d)
        {
            var list = new List<(bool, char, float)>();
            int i = 0, n = d.Length;
            while (i < n)
            {
                char c = d[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) { list.Add((true, c, 0f)); i++; continue; }

                int start = i;
                if (c == '+' || c == '-') i++;
                bool dot = false, exp = false;
                while (i < n)
                {
                    char q = d[i];
                    if (q >= '0' && q <= '9') i++;
                    else if (q == '.' && !dot && !exp) { dot = true; i++; }
                    else if ((q == 'e' || q == 'E') && !exp) { exp = true; i++; if (i < n && (d[i] == '+' || d[i] == '-')) i++; }
                    else break;
                }
                if (i == start) { i++; continue; }   // no progress → skip a stray char
                if (float.TryParse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    list.Add((false, '\0', val));
            }
            return list;
        }
    }
}
