using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using rtc.Models;

namespace rtc.Services;

/// <summary>
/// Hardware-accelerated native WPF canvas renderer for reMarkable 2 templates.
/// Renders 1404x1872 portrait or 1872x1404 landscape vectors, grid guidelines,
/// and the 9 reMarkable tablet colors directly to a WPF DrawingImage.
/// </summary>
public static class TabletPreviewRenderer
{
    private static readonly SolidColorBrush PaperBackground = Freeze(new SolidColorBrush(Color.FromRgb(250, 249, 245)));
    private static readonly Pen GridSlotPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(100, 148, 163, 184)), 1.5) { DashStyle = DashStyles.Dash });

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public static DrawingImage RenderToDrawingImage(
        RemarkableTemplate template,
        bool showGridSlots = true,
        bool snapToGrayscale = false)
    {
        bool isLandscape = string.Equals(template.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
        double tabletW = isLandscape ? 1872.0 : 1404.0;
        double tabletH = isLandscape ? 1404.0 : 1872.0;

        var drawingGroup = new DrawingGroup();
        using (var dc = drawingGroup.Open())
        {
            // 1. Draw E-Ink Carta paper background
            dc.DrawRectangle(PaperBackground, null, new Rect(0, 0, tabletW, tabletH));

            // 2. Resolve constants
            var vars = ResolveConstants(template, tabletW, tabletH);

            // 3. Draw grid slots if requested
            if (showGridSlots)
            {
                DrawGridGuidelines(dc, vars, tabletW, tabletH);
            }

            // 4. Render template items
            if (template.Items != null)
            {
                var ctx = new RenderContext(tabletW, tabletH, tabletW, tabletH, vars, snapToGrayscale);
                foreach (var item in template.Items)
                {
                    RenderItem(item, ctx, dc);
                }
            }
        }

        drawingGroup.Freeze();
        return new DrawingImage(drawingGroup);
    }

    private static void DrawGridGuidelines(DrawingContext dc, Dictionary<string, double> vars, double tabletW, double tabletH)
    {
        vars.TryGetValue("cols", out double cols);
        vars.TryGetValue("rows", out double rows);

        int cCount = Math.Max(1, (int)Math.Round(cols > 0 ? cols : 1));
        int rCount = Math.Max(1, (int)Math.Round(rows > 0 ? rows : 1));

        double slotW = tabletW / cCount;
        double slotH = tabletH / rCount;

        for (int c = 1; c < cCount; c++)
        {
            double x = c * slotW;
            dc.DrawLine(GridSlotPen, new Point(x, 0), new Point(x, tabletH));
        }

        for (int r = 1; r < rCount; r++)
        {
            double y = r * slotH;
            dc.DrawLine(GridSlotPen, new Point(0, y), new Point(tabletW, y));
        }
    }

    private sealed record RenderContext(
        double TemplateWidth,
        double TemplateHeight,
        double ParentWidth,
        double ParentHeight,
        Dictionary<string, double> Constants,
        bool SnapToGrayscale
    );

    private static void RenderItem(RemarkableItem item, RenderContext ctx, DrawingContext dc)
    {
        if (item is RemarkablePathItem pathItem)
        {
            var geometry = BuildStreamGeometry(pathItem.Data, ctx.Constants);
            if (geometry == null) return;

            var fillBrush = ParseBrush(pathItem.FillColor, ctx.SnapToGrayscale);
            var strokeBrush = ParseBrush(pathItem.StrokeColor, ctx.SnapToGrayscale);

            Pen? pen = null;
            if (strokeBrush != null)
            {
                double sw = pathItem.StrokeWidth > 0 ? pathItem.StrokeWidth : 1.0;
                pen = new Pen(strokeBrush, sw)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round
                };
                pen.Freeze();
            }

            dc.DrawGeometry(fillBrush, pen, geometry);
            return;
        }

        if (item is RemarkableGroupItem group)
        {
            var bb = group.BoundingBox;
            double bx = EvaluateExpression(bb.X?.ToString() ?? "0", ctx.Constants);
            double by = EvaluateExpression(bb.Y?.ToString() ?? "0", ctx.Constants);

            var wVars = new Dictionary<string, double>(ctx.Constants, StringComparer.OrdinalIgnoreCase)
            {
                ["parentWidth"] = ctx.ParentWidth,
                ["parentHeight"] = ctx.ParentHeight
            };

            double bw = EvaluateExpression(bb.Width?.ToString() ?? "parentWidth", wVars);
            double bh = EvaluateExpression(bb.Height?.ToString() ?? "parentHeight", wVars);

            int repeatCols = 1;
            int repeatRows = 1;
            if (group.Repeat?.Columns != null)
                repeatCols = Math.Max(1, (int)Math.Round(EvaluateExpression(group.Repeat.Columns.ToString() ?? "1", ctx.Constants)));
            if (group.Repeat?.Rows != null)
                repeatRows = Math.Max(1, (int)Math.Round(EvaluateExpression(group.Repeat.Rows.ToString() ?? "1", ctx.Constants)));

            for (int c = 0; c < repeatCols; c++)
            {
                for (int r = 0; r < repeatRows; r++)
                {
                    double offsetX = bx + c * bw;
                    double offsetY = by + r * bh;

                    bool hasOffset = Math.Abs(offsetX) > 0.0001 || Math.Abs(offsetY) > 0.0001;
                    if (hasOffset)
                    {
                        dc.PushTransform(new TranslateTransform(offsetX, offsetY));
                    }

                    var childVars = new Dictionary<string, double>(ctx.Constants, StringComparer.OrdinalIgnoreCase)
                    {
                        ["parentWidth"] = bw,
                        ["parentHeight"] = bh
                    };
                    var nextCtx = new RenderContext(ctx.TemplateWidth, ctx.TemplateHeight, bw, bh, childVars, ctx.SnapToGrayscale);

                    foreach (var child in group.Children)
                    {
                        RenderItem(child, nextCtx, dc);
                    }

                    if (hasOffset)
                    {
                        dc.Pop();
                    }
                }
            }
        }
    }

    private static StreamGeometry? BuildStreamGeometry(List<string> tokens, Dictionary<string, double> vars)
    {
        if (tokens == null || tokens.Count == 0) return null;

        var geom = new StreamGeometry();
        using (var gc = geom.Open())
        {
            int i = 0;
            bool figureOpen = false;
            Point figureStart = new Point();

            while (i < tokens.Count)
            {
                var cmd = tokens[i].Trim();
                if (cmd == "M")
                {
                    if (i + 2 >= tokens.Count) break;
                    double x = ParseCoord(tokens[i + 1], vars);
                    double y = ParseCoord(tokens[i + 2], vars);
                    figureStart = new Point(x, y);
                    gc.BeginFigure(figureStart, isFilled: true, isClosed: false);
                    figureOpen = true;
                    i += 3;
                }
                else if (cmd == "L")
                {
                    if (i + 2 >= tokens.Count) break;
                    double x = ParseCoord(tokens[i + 1], vars);
                    double y = ParseCoord(tokens[i + 2], vars);
                    var pt = new Point(x, y);
                    if (!figureOpen)
                    {
                        figureStart = pt;
                        gc.BeginFigure(figureStart, true, false);
                        figureOpen = true;
                    }
                    else
                    {
                        gc.LineTo(pt, isStroked: true, isSmoothJoin: true);
                    }
                    i += 3;
                }
                else if (cmd == "C")
                {
                    if (i + 6 >= tokens.Count) break;
                    double x1 = ParseCoord(tokens[i + 1], vars);
                    double y1 = ParseCoord(tokens[i + 2], vars);
                    double x2 = ParseCoord(tokens[i + 3], vars);
                    double y2 = ParseCoord(tokens[i + 4], vars);
                    double x = ParseCoord(tokens[i + 5], vars);
                    double y = ParseCoord(tokens[i + 6], vars);
                    if (!figureOpen)
                    {
                        figureStart = new Point(x1, y1);
                        gc.BeginFigure(figureStart, true, false);
                        figureOpen = true;
                    }
                    gc.BezierTo(new Point(x1, y1), new Point(x2, y2), new Point(x, y), isStroked: true, isSmoothJoin: true);
                    i += 7;
                }
                else if (cmd == "Z")
                {
                    if (figureOpen)
                    {
                        // Close the figure by drawing back to its start point.
                        gc.LineTo(figureStart, isStroked: true, isSmoothJoin: true);
                        figureOpen = false;
                    }
                    i += 1;
                }
                else
                {
                    i++;
                }
            }
        }

        geom.Freeze();
        return geom;
    }

    private static double ParseCoord(string token, Dictionary<string, double> vars)
    {
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return n;
        return EvaluateExpression(token, vars);
    }

    private static Brush? ParseBrush(string? hex, bool snapGrayscale)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Equals("none", StringComparison.OrdinalIgnoreCase) || hex.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;

        hex = hex.Trim();
        if (SvgToRemarkableConverter.Remarkable9Colors.TryGetValue(hex, out var exactHex))
            hex = exactHex;

        if (snapGrayscale)
        {
            hex = SvgToRemarkableConverter.NormalizeColorString(hex, snapToEink: true) ?? hex;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, double> ResolveConstants(RemarkableTemplate template, double canvasW, double canvasH)
    {
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["templateWidth"] = canvasW,
            ["templateHeight"] = canvasH
        };

        if (template.Constants != null)
        {
            foreach (var dict in template.Constants)
            {
                foreach (var (key, rawVal) in dict)
                {
                    if (rawVal is System.Text.Json.JsonElement je)
                    {
                        if (je.ValueKind == System.Text.Json.JsonValueKind.Number)
                            vars[key] = je.GetDouble();
                        else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                            vars[key] = EvaluateExpression(je.GetString() ?? "0", vars);
                    }
                    else if (rawVal is double d)
                    {
                        vars[key] = d;
                    }
                    else if (rawVal is int valInt)
                    {
                        vars[key] = valInt;
                    }
                    else if (rawVal is string s)
                    {
                        vars[key] = EvaluateExpression(s, vars);
                    }
                }
            }
        }

        return vars;
    }

    public static double EvaluateExpression(string expr, Dictionary<string, double> vars)
    {
        if (string.IsNullOrWhiteSpace(expr)) return 0;
        if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;

        // Pattern: dx + (val)*s or dy + (val)*s
        var paramMatch = Regex.Match(expr, @"^\s*d([xy])\s*\+\s*\(([^)]+)\)\s*\*\s*s\s*$", RegexOptions.IgnoreCase);
        if (paramMatch.Success)
        {
            string axis = "d" + paramMatch.Groups[1].Value.ToLowerInvariant();
            vars.TryGetValue(axis, out double dAxis);
            vars.TryGetValue("s", out double scale);
            if (double.TryParse(paramMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double innerVal))
            {
                return dAxis + innerVal * scale;
            }
        }

        var minMatch = Regex.Match(expr, @"min\(([^,]+),([^)]+)\)", RegexOptions.IgnoreCase);
        if (minMatch.Success)
        {
            double a = EvaluateExpression(minMatch.Groups[1].Value, vars);
            double b = EvaluateExpression(minMatch.Groups[2].Value, vars);
            return Math.Min(a, b);
        }

        var keys = new List<string>(vars.Keys);
        keys.Sort((a, b) => b.Length.CompareTo(a.Length));

        string substituted = expr;
        foreach (var k in keys)
        {
            var pattern = $@"\b{Regex.Escape(k)}\b";
            substituted = Regex.Replace(substituted, pattern, vars[k].ToString("R", CultureInfo.InvariantCulture));
        }

        try
        {
            var table = new System.Data.DataTable { Locale = CultureInfo.InvariantCulture };
            var val = table.Compute(substituted, string.Empty);
            return Convert.ToDouble(val, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}
