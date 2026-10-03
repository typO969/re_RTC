using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using rtc.Models;

namespace rtc.Services;

/// <summary>
/// Reverse conversion engine: reMarkable *.template / JSON => standard SVG vector graphic.
/// Reconstructs SVG paths from procedural tokens, resolves repeat matrices, and formats clean SVG XML.
/// Fully supports both Portrait (1404x1872) and Landscape (1872x1404) orientations.
/// </summary>
public static class RemarkableToSvgConverter
{
    public static async Task<string> ConvertTemplateFileToSvgAsync(string templateFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(templateFilePath))
            throw new FileNotFoundException("Template file not found.", templateFilePath);

        var json = await File.ReadAllTextAsync(templateFilePath, ct).ConfigureAwait(false);
        var template = System.Text.Json.JsonSerializer.Deserialize<RemarkableTemplate>(json)
            ?? throw new InvalidOperationException("Could not deserialize template.");

        return await Task.Run(() => ConvertTemplateToSvgString(template), ct).ConfigureAwait(false);
    }

    public static string ConvertTemplateToSvgString(RemarkableTemplate template)
    {
        bool isLandscape = string.Equals(template.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
        int tabletW = isLandscape ? 1872 : 1404;
        int tabletH = isLandscape ? 1404 : 1872;

        var vars = ResolveConstants(template, tabletW, tabletH);

        XNamespace ns = "http://www.w3.org/2000/svg";
        var root = new XElement(ns + "svg",
            new XAttribute("xmlns", ns.NamespaceName),
            new XAttribute("viewBox", $"0 0 {tabletW} {tabletH}"),
            new XAttribute("width", tabletW),
            new XAttribute("height", tabletH),
            new XAttribute("data-template-name", template.Name ?? "template"),
            new XAttribute("data-orientation", template.Orientation ?? (isLandscape ? "landscape" : "portrait"))
        );

        root.Add(new XComment($" Reconstructed by re_RTC from reMarkable template: {template.Name} ({template.Orientation}) "));

        var ctx = new SvgRenderContext(tabletW, tabletH, tabletW, tabletH, vars);

        if (template.Items != null)
        {
            foreach (var item in template.Items)
            {
                RenderItemToSvg(item, ctx, root, ns);
            }
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        {
            doc.Save(writer);
        }
        return sb.ToString();
    }

    private sealed record SvgRenderContext(
        double TemplateWidth,
        double TemplateHeight,
        double ParentWidth,
        double ParentHeight,
        Dictionary<string, double> Constants
    );

    private static void RenderItemToSvg(RemarkableItem item, SvgRenderContext ctx, XElement parent, XNamespace ns)
    {
        if (item is RemarkablePathItem pathItem)
        {
            var pathD = BuildSvgPathData(pathItem.Data, ctx.Constants);
            if (string.IsNullOrWhiteSpace(pathD)) return;

            var el = new XElement(ns + "path", new XAttribute("d", pathD));

            var fill = pathItem.FillColor;
            if (!string.IsNullOrWhiteSpace(fill) && !fill.Equals("none", StringComparison.OrdinalIgnoreCase) && !fill.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                el.Add(new XAttribute("fill", fill));
            }
            else
            {
                el.Add(new XAttribute("fill", "none"));
            }

            var stroke = pathItem.StrokeColor;
            if (!string.IsNullOrWhiteSpace(stroke) && !stroke.Equals("none", StringComparison.OrdinalIgnoreCase) && !stroke.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                el.Add(new XAttribute("stroke", stroke));
                double sw = pathItem.StrokeWidth > 0 ? pathItem.StrokeWidth : 1.0;
                el.Add(new XAttribute("stroke-width", sw.ToString("0.####", CultureInfo.InvariantCulture)));
                el.Add(new XAttribute("stroke-linecap", "round"));
                el.Add(new XAttribute("stroke-linejoin", "round"));
            }
            else
            {
                el.Add(new XAttribute("stroke", "none"));
            }

            parent.Add(el);
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

                    var gEl = new XElement(ns + "g");
                    if (Math.Abs(offsetX) > 0.0001 || Math.Abs(offsetY) > 0.0001)
                    {
                        gEl.Add(new XAttribute("transform", $"translate({offsetX.ToString("0.####", CultureInfo.InvariantCulture)}, {offsetY.ToString("0.####", CultureInfo.InvariantCulture)})"));
                    }

                    var childVars = new Dictionary<string, double>(ctx.Constants, StringComparer.OrdinalIgnoreCase)
                    {
                        ["parentWidth"] = bw,
                        ["parentHeight"] = bh
                    };
                    var nextCtx = new SvgRenderContext(ctx.TemplateWidth, ctx.TemplateHeight, bw, bh, childVars);

                    foreach (var child in group.Children)
                    {
                        RenderItemToSvg(child, nextCtx, gEl, ns);
                    }

                    parent.Add(gEl);
                }
            }
        }
    }

    private static string BuildSvgPathData(List<string> tokens, Dictionary<string, double> vars)
    {
        if (tokens == null || tokens.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        int i = 0;

        while (i < tokens.Count)
        {
            var cmd = tokens[i].Trim();
            if (cmd == "M")
            {
                if (i + 2 >= tokens.Count) break;
                double x = ParseCoordToken(tokens[i + 1], vars);
                double y = ParseCoordToken(tokens[i + 2], vars);
                sb.Append($"M {x.ToString("0.####", CultureInfo.InvariantCulture)} {y.ToString("0.####", CultureInfo.InvariantCulture)} ");
                i += 3;
            }
            else if (cmd == "L")
            {
                if (i + 2 >= tokens.Count) break;
                double x = ParseCoordToken(tokens[i + 1], vars);
                double y = ParseCoordToken(tokens[i + 2], vars);
                sb.Append($"L {x.ToString("0.####", CultureInfo.InvariantCulture)} {y.ToString("0.####", CultureInfo.InvariantCulture)} ");
                i += 3;
            }
            else if (cmd == "C")
            {
                if (i + 6 >= tokens.Count) break;
                double x1 = ParseCoordToken(tokens[i + 1], vars);
                double y1 = ParseCoordToken(tokens[i + 2], vars);
                double x2 = ParseCoordToken(tokens[i + 3], vars);
                double y2 = ParseCoordToken(tokens[i + 4], vars);
                double x = ParseCoordToken(tokens[i + 5], vars);
                double y = ParseCoordToken(tokens[i + 6], vars);
                sb.Append($"C {x1.ToString("0.####", CultureInfo.InvariantCulture)} {y1.ToString("0.####", CultureInfo.InvariantCulture)} " +
                          $"{x2.ToString("0.####", CultureInfo.InvariantCulture)} {y2.ToString("0.####", CultureInfo.InvariantCulture)} " +
                          $"{x.ToString("0.####", CultureInfo.InvariantCulture)} {y.ToString("0.####", CultureInfo.InvariantCulture)} ");
                i += 7;
            }
            else if (cmd == "Z")
            {
                sb.Append("Z ");
                i += 1;
            }
            else
            {
                i++;
            }
        }

        return sb.ToString().Trim();
    }

    private static double ParseCoordToken(string token, Dictionary<string, double> vars)
    {
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var directNum))
            return directNum;

        return EvaluateExpression(token, vars);
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

        // Handle pattern: dx + (val)*s or dy + (val)*s
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

        // Handle min(a, b)
        var minMatch = Regex.Match(expr, @"min\(([^,]+),([^)]+)\)", RegexOptions.IgnoreCase);
        if (minMatch.Success)
        {
            double a = EvaluateExpression(minMatch.Groups[1].Value, vars);
            double b = EvaluateExpression(minMatch.Groups[2].Value, vars);
            return Math.Min(a, b);
        }

        // Substitute known variables
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
