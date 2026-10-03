using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using rtc.Models;

namespace rtc.Services;

/// <summary>
/// Provides programmatic template grid transformation for reMarkable 2.
/// Features the 1×Full Standard Master Blueprint: author once at full scale (1404x1872 / 1872x1404),
/// and the procedural rescaler derives 3x4, 4x5, and 5x6 matrices automatically.
/// Exports native *.template files for direct tablet deployment.
/// </summary>
public static class TemplateGridResizer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public record GridDimensions(int Cols, int Rows, bool IsParametric);

    /// <summary>
    /// Inspects an existing template to detect its current grid dimensions.
    /// </summary>
    public static GridDimensions DetectGrid(RemarkableTemplate template)
    {
        int? cols = null;
        int? rows = null;
        bool isParametric = false;

        // Check constants list
        foreach (var dict in template.Constants)
        {
            if (dict.TryGetValue("cols", out var cVal) && cVal is JsonElement { ValueKind: JsonValueKind.Number } ce)
            {
                cols = ce.GetInt32();
                isParametric = true;
            }
            if (dict.TryGetValue("rows", out var rVal) && rVal is JsonElement { ValueKind: JsonValueKind.Number } re)
            {
                rows = re.GetInt32();
                isParametric = true;
            }
        }

        // Check name heuristic (e.g. "1xFull", "3x4", "4x5", "5x6")
        if (cols == null || rows == null)
        {
            if (Regex.IsMatch(template.Name, @"1xFull|1x1", RegexOptions.IgnoreCase))
            {
                cols = 1;
                rows = 1;
            }
            else
            {
                var match = Regex.Match(template.Name, @"(d+)s*[xX_×]s*(d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var c) && int.TryParse(match.Groups[2].Value, out var r))
                {
                    cols ??= c;
                    rows ??= r;
                }
            }
        }

        return new GridDimensions(cols ?? 3, rows ?? 4, isParametric);
    }

    /// <summary>
    /// Gets formatted name reflecting the target grid (e.g. pictograph_1xFull_master or pictograph_3x4_master).
    /// </summary>
    public static string GetResizedTemplateName(string currentName, int targetCols, int targetRows)
    {
        var targetTag = (targetCols == 1 && targetRows == 1) ? "1xFull" : $"{targetCols}x{targetRows}";
        if (Regex.IsMatch(currentName, @"(d+)s*[xX_×]s*(d+)|1xFull", RegexOptions.IgnoreCase))
        {
            return Regex.Replace(currentName, @"(d+)s*[xX_×]s*(d+)|1xFull", targetTag, RegexOptions.IgnoreCase);
        }
        return $"{currentName}_{targetTag}";
    }

    /// <summary>
    /// Transforms any template into target columns and rows (e.g., 1xFull Master, 3x4, 4x5, 5x6).
    /// </summary>
    public static RemarkableTemplate TransformGrid(RemarkableTemplate source, int targetCols, int targetRows)
    {
        // Deep clone via JSON
        var json = JsonSerializer.Serialize(source, JsonOptions);
        var cloned = JsonSerializer.Deserialize<RemarkableTemplate>(json, JsonOptions) 
                     ?? throw new InvalidOperationException("Failed to clone template.");

        cloned.Name = GetResizedTemplateName(cloned.Name, targetCols, targetRows);

        // Update constants
        bool colsFound = false;
        bool rowsFound = false;

        foreach (var entry in cloned.Constants)
        {
            if (entry.ContainsKey("cols") || entry.ContainsKey("columns"))
            {
                entry["cols"] = targetCols;
                colsFound = true;
            }
            if (entry.ContainsKey("rows"))
            {
                entry["rows"] = targetRows;
                rowsFound = true;
            }
        }

        if (!colsFound) cloned.Constants.Insert(0, new Dictionary<string, object> { ["cols"] = targetCols });
        if (!rowsFound) cloned.Constants.Insert(1, new Dictionary<string, object> { ["rows"] = targetRows });

        // Update group repeat properties if hardcoded
        UpdateGroupRepeats(cloned.Items, targetCols, targetRows);

        return cloned;
    }

    private static void UpdateGroupRepeats(List<RemarkableItem> items, int targetCols, int targetRows)
    {
        foreach (var item in items)
        {
            if (item is RemarkableGroupItem group)
            {
                if (group.Repeat != null)
                {
                    if (group.Repeat.Columns is int) group.Repeat.Columns = targetCols;
                    if (group.Repeat.Rows is int) group.Repeat.Rows = targetRows;
                }
                UpdateGroupRepeats(group.Children, targetCols, targetRows);
            }
        }
    }

    /// <summary>
    /// Generates all 4 standard sizes: 1xFull Standard Master + 3 procedural grids (3x4, 4x5, 5x6).
    /// </summary>
    public static Dictionary<string, RemarkableTemplate> GenerateAllStandardSizes(RemarkableTemplate source)
    {
        return new Dictionary<string, RemarkableTemplate>
        {
            ["1xFull"] = TransformGrid(source, 1, 1),
            ["3x4"] = TransformGrid(source, 3, 4),
            ["4x5"] = TransformGrid(source, 4, 5),
            ["5x6"] = TransformGrid(source, 5, 6),
        };
    }

    /// <summary>
    /// Generates quantized template variants strictly adhering to the 4 Template Type specifications:
    /// - CURSIVE: Landscape; 1xFull, 3x4 (4 forms across columns), 4x5 (4 forms down rows; smallest allowed)
    /// - HAND: Portrait; 1xFull, 3x3, 4x4, 5x5 (page ratio equal numbers)
    /// - LATIN: Portrait; 1xFull, 3x3, 4x4, 5x5
    /// - PICTOGRAPH: Portrait; 1xFull, 3x4, 4x5, 5x6, 6x7 (high capacity)
    /// </summary>
    public static Dictionary<string, RemarkableTemplate> GenerateSizesForTemplateType(RemarkableTemplate source, string templateType = "pictograph")
    {
        return templateType.ToLowerInvariant() switch
        {
            "cursive" => new Dictionary<string, RemarkableTemplate>
            {
                ["1xFull"] = TransformGrid(source, 1, 1),
                ["3x4"] = TransformGrid(source, 4, 3), // 4 form columns across x 3 letter rows
                ["4x5"] = TransformGrid(source, 5, 4), // 5 letter columns x 4 form rows down (smallest allowed)
            },
            "hand" => new Dictionary<string, RemarkableTemplate>
            {
                ["1xFull"] = TransformGrid(source, 1, 1),
                ["3x3"] = TransformGrid(source, 3, 3),
                ["4x4"] = TransformGrid(source, 4, 4),
                ["5x5"] = TransformGrid(source, 5, 5),
            },
            "latin" => new Dictionary<string, RemarkableTemplate>
            {
                ["1xFull"] = TransformGrid(source, 1, 1),
                ["3x3"] = TransformGrid(source, 3, 3),
                ["4x4"] = TransformGrid(source, 4, 4),
                ["5x5"] = TransformGrid(source, 5, 5),
            },
            _ => new Dictionary<string, RemarkableTemplate>
            {
                ["1xFull"] = TransformGrid(source, 1, 1),
                ["3x4"] = TransformGrid(source, 3, 4),
                ["4x5"] = TransformGrid(source, 4, 5),
                ["5x6"] = TransformGrid(source, 5, 6),
                ["6x7"] = TransformGrid(source, 6, 7),
            },
        };
    }

    /// <summary>
    /// Exports all standard sizes into an output directory as native *.template files.
    /// </summary>
    public static async Task ExportAllStandardSizesAsync(RemarkableTemplate source, string outputDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);
        var variants = GenerateAllStandardSizes(source);

        foreach (var (sizeKey, tpl) in variants)
        {
            var filePath = Path.Combine(outputDir, $"{tpl.Name}.template");
            var json = JsonSerializer.Serialize(tpl, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct).ConfigureAwait(false);
        }
    }
}
