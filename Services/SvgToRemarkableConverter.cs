using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using rtc.Models;

namespace rtc.Services;

/// <summary>
/// Thread-safe SVG-to-reMarkable conversion engine targeting .NET 10 LTS.
/// Supports cubic Bezier paths, 3x4 / 4x5 / 5x6 grid matrices, and the 9 reMarkable tablet colors.
/// </summary>
public static class SvgToRemarkableConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly Dictionary<string, string> Remarkable9Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["gray"] = "#909090",
        ["white"] = "#FFFFFF",
        ["blue"] = "#4E69C9",
        ["red"] = "#B33E39",
        ["green"] = "#A1D87D",
        ["yellow"] = "#F7E851",
        ["cyan"] = "#8BD0E5",
        ["magenta"] = "#B782CD",
    };

    public static async Task<string> ConvertSvgFileToJsonAsync(string filePath, ConversionSettings settings, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("SVG file does not exist.", filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var template = await Task.Run(() => ConvertSvgStringToTemplate(content, settings), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(template, JsonOptions);
    }

    public static async Task<RemarkableTemplate> ConvertSvgFileToTemplateAsync(string filePath, ConversionSettings settings, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("SVG file does not exist.", filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return await Task.Run(() => ConvertSvgStringToTemplate(content, settings), cancellationToken).ConfigureAwait(false);
    }

    public static RemarkableTemplate ConvertSvgStringToTemplate(string svgContent, ConversionSettings settings)
    {
        var xDoc = XDocument.Parse(svgContent);
        var root = xDoc.Root ?? throw new InvalidOperationException("Invalid SVG: missing root element.");

        // Determine viewBox dimensions (defaults based on orientation: 1404x1872 portrait or 1872x1404 landscape)
        bool isLandscape = string.Equals(settings.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
        double hudW = isLandscape ? 1872.0 : 1404.0;
        double hudH = isLandscape ? 1404.0 : 1872.0;

        var viewBoxAttr = root.Attribute("viewBox")?.Value;
        if (!string.IsNullOrWhiteSpace(viewBoxAttr))
        {
            var parts = viewBoxAttr.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
            {
                hudW = vw;
                hudH = vh;
            }
        }
        else
        {
            if (double.TryParse(root.Attribute("width")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) hudW = w;
            if (double.TryParse(root.Attribute("height")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) hudH = h;
        }

        var pathItems = new List<RemarkablePathItem>();

        // Traverse geometric nodes
        foreach (var el in root.Descendants())
        {
            var tag = el.Name.LocalName.ToLowerInvariant();
            string? pathD = tag switch
            {
                "path" => el.Attribute("d")?.Value,
                "rect" => ConvertRectToPathD(el),
                "circle" => ConvertCircleToPathD(el),
                "ellipse" => ConvertEllipseToPathD(el),
                "line" => ConvertLineToPathD(el),
                "polyline" => ConvertPolyToPathD(el, isClosed: false),
                "polygon" => ConvertPolyToPathD(el, isClosed: true),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(pathD)) continue;

            var tokens = TokenizeAndNormalizePathData(pathD, settings);
            if (tokens.Count == 0) continue;

            var (fill, stroke, strokeWidth) = ExtractStrokeAndFill(el, settings);

            pathItems.Add(new RemarkablePathItem
            {
                Data = tokens,
                FillColor = fill,
                StrokeColor = stroke,
                StrokeWidth = strokeWidth,
                Antialiasing = true
            });
        }

        // Build template
        var template = new RemarkableTemplate
        {
            Name = settings.TemplateName,
            Author = settings.Author,
            TemplateVersion = "1.0.0",
            FormatVersion = 1,
            Orientation = settings.Orientation,
            Categories = ["Custom"],
            Constants = BuildConstantsList(settings, hudW, hudH)
        };

        if (settings.GridCols == 1 && settings.GridRows == 1)
        {
            // 1xFull Master: items placed directly or in single 1x1 group
            template.Items =
            [
                new RemarkableGroupItem
                {
                    BoundingBox = new RemarkableBoundingBox
                    {
                        X = 0,
                        Y = 0,
                        Width = "parentWidth",
                        Height = "parentHeight"
                    },
                    Repeat = new RemarkableRepeat
                    {
                        Columns = "cols",
                        Rows = "rows"
                    },
                    Children = pathItems.Cast<RemarkableItem>().ToList()
                }
            ];
        }
        else
        {
            // Procedural grid container
            template.Items =
            [
                new RemarkableGroupItem
                {
                    BoundingBox = new RemarkableBoundingBox
                    {
                        X = 0,
                        Y = 0,
                        Width = "slotW",
                        Height = "slotH"
                    },
                    Repeat = new RemarkableRepeat
                    {
                        Columns = "cols",
                        Rows = "rows"
                    },
                    Children = pathItems.Cast<RemarkableItem>().ToList()
                }
            ];
        }

        return template;
    }

    private static List<Dictionary<string, object>> BuildConstantsList(ConversionSettings s, double hudW, double hudH)
    {
        return
        [
            new() { ["cols"] = s.GridCols },
            new() { ["rows"] = s.GridRows },
            new() { ["slotW"] = "(templateWidth) / (cols)" },
            new() { ["slotH"] = "(templateHeight) / (rows)" },
            new() { ["hudW"] = hudW },
            new() { ["hudH"] = hudH },
            new() { ["s"] = "min(slotW/hudW, slotH/hudH)" },
            new() { ["dx"] = "(slotW - hudW*s)/2" },
            new() { ["dy"] = "(slotH - hudH*s)/2" }
        ];
    }

    #region Path Normalization
    private static List<string> TokenizeAndNormalizePathData(string pathD, ConversionSettings settings)
    {
        var tokens = new List<string>();
        var format = "0." + new string('#', settings.DecimalPrecision);

        var matches = Regex.Matches(pathD, @"([a-zA-Z])|([+-]?(?:\d+\.\d+|\.\d+|\d+)(?:[eE][+-]?\d+)?)");
        var raw = new List<(char? cmd, double number)>();

        foreach (Match m in matches)
        {
            if (m.Groups[1].Success)
            {
                raw.Add((m.Groups[1].Value[0], 0));
            }
            else if (m.Groups[2].Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                raw.Add((null, n));
            }
        }

        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        double lastControlX = 0, lastControlY = 0;
        string lastCmd = string.Empty;

        int i = 0;
        while (i < raw.Count)
        {
            var item = raw[i];
            if (!item.cmd.HasValue) { i++; continue; }

            char cmd = item.cmd.Value;
            char cmdUpper = char.ToUpperInvariant(cmd);
            bool isRel = char.IsLower(cmd);
            i++;

            switch (cmdUpper)
            {
                case 'M':
                {
                    double x = isRel ? curX + raw[i].number : raw[i].number;
                    double y = isRel ? curY + raw[i + 1].number : raw[i + 1].number;
                    i += 2;
                    curX = x; curY = y;
                    startX = x; startY = y;
                    lastControlX = x; lastControlY = y;

                    tokens.Add("M");
                    tokens.Add(FormatCoord(curX, "dx", format, settings));
                    tokens.Add(FormatCoord(curY, "dy", format, settings));
                    break;
                }
                case 'L':
                {
                    double x = isRel ? curX + raw[i].number : raw[i].number;
                    double y = isRel ? curY + raw[i + 1].number : raw[i + 1].number;
                    i += 2;
                    curX = x; curY = y;
                    lastControlX = x; lastControlY = y;

                    tokens.Add("L");
                    tokens.Add(FormatCoord(curX, "dx", format, settings));
                    tokens.Add(FormatCoord(curY, "dy", format, settings));
                    break;
                }
                case 'H':
                {
                    double x = isRel ? curX + raw[i].number : raw[i].number;
                    i += 1;
                    curX = x;
                    lastControlX = x;

                    tokens.Add("L");
                    tokens.Add(FormatCoord(curX, "dx", format, settings));
                    tokens.Add(FormatCoord(curY, "dy", format, settings));
                    break;
                }
                case 'V':
                {
                    double y = isRel ? curY + raw[i].number : raw[i].number;
                    i += 1;
                    curY = y;
                    lastControlY = y;

                    tokens.Add("L");
                    tokens.Add(FormatCoord(curX, "dx", format, settings));
                    tokens.Add(FormatCoord(curY, "dy", format, settings));
                    break;
                }
                case 'C':
                {
                    if (i + 5 >= raw.Count) break;
                    double x1 = isRel ? curX + raw[i].number : raw[i].number;
                    double y1 = isRel ? curY + raw[i + 1].number : raw[i + 1].number;
                    double x2 = isRel ? curX + raw[i + 2].number : raw[i + 2].number;
                    double y2 = isRel ? curY + raw[i + 3].number : raw[i + 3].number;
                    double x = isRel ? curX + raw[i + 4].number : raw[i + 4].number;
                    double y = isRel ? curY + raw[i + 5].number : raw[i + 5].number;
                    i += 6;
                    lastControlX = x2; lastControlY = y2;
                    curX = x; curY = y;

                    tokens.Add("C");
                    tokens.Add(FormatCoord(x1, "dx", format, settings));
                    tokens.Add(FormatCoord(y1, "dy", format, settings));
                    tokens.Add(FormatCoord(x2, "dx", format, settings));
                    tokens.Add(FormatCoord(y2, "dy", format, settings));
                    tokens.Add(FormatCoord(x, "dx", format, settings));
                    tokens.Add(FormatCoord(y, "dy", format, settings));
                    break;
                }
                case 'S':
                {
                    // Smooth cubic Bezier
                    if (i + 3 >= raw.Count) break;
                    double x1 = curX;
                    double y1 = curY;
                    if (lastCmd == "C" || lastCmd == "S")
                    {
                        x1 = 2 * curX - lastControlX;
                        y1 = 2 * curY - lastControlY;
                    }
                    double x2 = isRel ? curX + raw[i].number : raw[i].number;
                    double y2 = isRel ? curY + raw[i + 1].number : raw[i + 1].number;
                    double x = isRel ? curX + raw[i + 2].number : raw[i + 2].number;
                    double y = isRel ? curY + raw[i + 3].number : raw[i + 3].number;
                    i += 4;
                    lastControlX = x2; lastControlY = y2;
                    curX = x; curY = y;

                    tokens.Add("C");
                    tokens.Add(FormatCoord(x1, "dx", format, settings));
                    tokens.Add(FormatCoord(y1, "dy", format, settings));
                    tokens.Add(FormatCoord(x2, "dx", format, settings));
                    tokens.Add(FormatCoord(y2, "dy", format, settings));
                    tokens.Add(FormatCoord(x, "dx", format, settings));
                    tokens.Add(FormatCoord(y, "dy", format, settings));
                    break;
                }
                case 'Q':
                {
                    // Elevate quadratic to cubic Bezier
                    if (i + 3 >= raw.Count) break;
                    double qx = isRel ? curX + raw[i].number : raw[i].number;
                    double qy = isRel ? curY + raw[i + 1].number : raw[i + 1].number;
                    double x = isRel ? curX + raw[i + 2].number : raw[i + 2].number;
                    double y = isRel ? curY + raw[i + 3].number : raw[i + 3].number;
                    i += 4;

                    double x1 = curX + (2.0 / 3.0) * (qx - curX);
                    double y1 = curY + (2.0 / 3.0) * (qy - curY);
                    double x2 = x + (2.0 / 3.0) * (qx - x);
                    double y2 = y + (2.0 / 3.0) * (qy - y);

                    lastControlX = qx; lastControlY = qy;
                    curX = x; curY = y;

                    tokens.Add("C");
                    tokens.Add(FormatCoord(x1, "dx", format, settings));
                    tokens.Add(FormatCoord(y1, "dy", format, settings));
                    tokens.Add(FormatCoord(x2, "dx", format, settings));
                    tokens.Add(FormatCoord(y2, "dy", format, settings));
                    tokens.Add(FormatCoord(x, "dx", format, settings));
                    tokens.Add(FormatCoord(y, "dy", format, settings));
                    break;
                }
                case 'Z':
                {
                    tokens.Add("Z");
                    curX = startX;
                    curY = startY;
                    lastControlX = curX;
                    lastControlY = curY;
                    break;
                }
                default:
                    i++;
                    break;
            }
            lastCmd = cmdUpper.ToString();
        }

        return tokens;
    }

    private static string FormatCoord(double val, string axisOffset, string format, ConversionSettings settings)
    {
        var strVal = val.ToString(format, CultureInfo.InvariantCulture);
        return settings.UseParametricScaling ? $"{axisOffset} + ({strVal})*s" : strVal;
    }
    #endregion

    #region Shape Converters
    private static string ConvertRectToPathD(XElement el)
    {
        double.TryParse(el.Attribute("x")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x);
        double.TryParse(el.Attribute("y")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y);
        double.TryParse(el.Attribute("width")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var w);
        double.TryParse(el.Attribute("height")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h);

        if (w <= 0 || h <= 0) return string.Empty;
        return $"M {x} {y} L {x + w} {y} L {x + w} {y + h} L {x} {y + h} Z";
    }

    private static string ConvertCircleToPathD(XElement el)
    {
        double.TryParse(el.Attribute("cx")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cx);
        double.TryParse(el.Attribute("cy")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cy);
        double.TryParse(el.Attribute("r")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var r);

        if (r <= 0) return string.Empty;
        const double k = 0.5522847498;
        return $"M {cx + r} {cy} " +
               $"C {cx + r} {cy + k * r} {cx + k * r} {cy + r} {cx} {cy + r} " +
               $"C {cx - k * r} {cy + r} {cx - r} {cy + k * r} {cx - r} {cy} " +
               $"C {cx - r} {cy - k * r} {cx - k * r} {cy - r} {cx} {cy - r} " +
               $"C {cx + k * r} {cy - r} {cx + r} {cy - k * r} {cx + r} {cy} Z";
    }

    private static string ConvertEllipseToPathD(XElement el)
    {
        double.TryParse(el.Attribute("cx")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cx);
        double.TryParse(el.Attribute("cy")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cy);
        double.TryParse(el.Attribute("rx")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rx);
        double.TryParse(el.Attribute("ry")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ry);

        if (rx <= 0 || ry <= 0) return string.Empty;
        const double k = 0.5522847498;
        return $"M {cx + rx} {cy} " +
               $"C {cx + rx} {cy + k * ry} {cx + k * rx} {cy + ry} {cx} {cy + ry} " +
               $"C {cx - k * rx} {cy + ry} {cx - rx} {cy + k * ry} {cx - rx} {cy} " +
               $"C {cx - rx} {cy - k * ry} {cx - k * rx} {cy - ry} {cx} {cy - ry} " +
               $"C {cx + k * rx} {cy - ry} {cx + rx} {cy - k * ry} {cx + rx} {cy} Z";
    }

    private static string ConvertLineToPathD(XElement el)
    {
        double.TryParse(el.Attribute("x1")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x1);
        double.TryParse(el.Attribute("y1")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y1);
        double.TryParse(el.Attribute("x2")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x2);
        double.TryParse(el.Attribute("y2")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y2);

        return $"M {x1} {y1} L {x2} {y2}";
    }

    private static string ConvertPolyToPathD(XElement el, bool isClosed)
    {
        var ptsStr = el.Attribute("points")?.Value;
        if (string.IsNullOrWhiteSpace(ptsStr)) return string.Empty;

        var parts = ptsStr.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return string.Empty;

        var sb = new StringBuilder();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                sb.Append(i == 0 ? $"M {x} {y} " : $"L {x} {y} ");
            }
        }

        if (isClosed) sb.Append('Z');
        return sb.ToString().Trim();
    }

    private static (string? fill, string? stroke, double strokeWidth) ExtractStrokeAndFill(XElement el, ConversionSettings settings)
    {
        var fill = el.Attribute("fill")?.Value;
        var stroke = el.Attribute("stroke")?.Value;
        double strokeWidth = 0;

        if (double.TryParse(el.Attribute("stroke-width")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sw))
        {
            strokeWidth = sw;
        }

        // Also check inline style="..." attribute commonly generated by Inkscape / Illustrator
        var style = el.Attribute("style")?.Value;
        if (!string.IsNullOrWhiteSpace(style))
        {
            var parts = style.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split(':', 2);
                if (kv.Length == 2)
                {
                    var k = kv[0].Trim().ToLowerInvariant();
                    var v = kv[1].Trim();
                    if (k == "fill" && string.IsNullOrWhiteSpace(fill)) fill = v;
                    else if (k == "stroke" && string.IsNullOrWhiteSpace(stroke)) stroke = v;
                    else if (k == "stroke-width" && strokeWidth == 0 && double.TryParse(v.Replace("px", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var styleSw))
                    {
                        strokeWidth = styleSw;
                    }
                }
            }
        }

        fill = NormalizeColorString(fill, settings.SnapToEinkGrayscale);
        stroke = NormalizeColorString(stroke, settings.SnapToEinkGrayscale);

        if (fill == null && stroke == null)
        {
            stroke = "#000000";
            strokeWidth = settings.DefaultStrokeWidth;
        }

        return (fill, stroke, strokeWidth);
    }

    public static string? NormalizeColorString(string? color, bool snapToEink)
    {
        if (string.IsNullOrWhiteSpace(color) || color.Equals("none", StringComparison.OrdinalIgnoreCase) || color.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return null;

        color = color.Trim().ToUpperInvariant();
        if (Remarkable9Colors.TryGetValue(color, out var exactHex))
            color = exactHex;

        if (snapToEink && color.StartsWith('#') && color.Length >= 7)
        {
            try
            {
                int r = Convert.ToInt32(color.Substring(1, 2), 16);
                int g = Convert.ToInt32(color.Substring(3, 2), 16);
                int b = Convert.ToInt32(color.Substring(5, 2), 16);
                int lum = (int)(0.299 * r + 0.587 * g + 0.114 * b);

                if (lum < 40) return "#000000";
                if (lum < 110) return "#606060";
                if (lum < 160) return "#909090";
                if (lum < 205) return "#C0C0C0";
                if (lum < 240) return "#E0E0E0";
                return "#FFFFFF";
            }
            catch
            {
                // Fallthrough
            }
        }

        return color;
    }
    #endregion
}
