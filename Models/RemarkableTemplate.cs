using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace rtc.Models;

public class RemarkableTemplate
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "template";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "Author";

    [JsonPropertyName("templateVersion")]
    public string TemplateVersion { get; set; } = "1.0.0";

    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = ["Custom"];

    [JsonPropertyName("orientation")]
    public string Orientation { get; set; } = "portrait"; // "portrait" or "landscape"

    [JsonPropertyName("constants")]
    public List<Dictionary<string, object>> Constants { get; set; } = [];

    [JsonPropertyName("items")]
    public List<RemarkableItem> Items { get; set; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RemarkableGroupItem), "group")]
[JsonDerivedType(typeof(RemarkablePathItem), "path")]
public abstract class RemarkableItem
{
    [JsonIgnore]
    public abstract string Type { get; }
}

public class RemarkableGroupItem : RemarkableItem
{
    [JsonIgnore]
    public override string Type => "group";

    [JsonPropertyName("boundingBox")]
    public RemarkableBoundingBox BoundingBox { get; set; } = new();

    [JsonPropertyName("repeat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemarkableRepeat? Repeat { get; set; }

    [JsonPropertyName("children")]
    public List<RemarkableItem> Children { get; set; } = [];
}

public class RemarkableBoundingBox
{
    [JsonPropertyName("x")]
    public object X { get; set; } = 0;

    [JsonPropertyName("y")]
    public object Y { get; set; } = 0;

    [JsonPropertyName("width")]
    public object Width { get; set; } = "parentWidth";

    [JsonPropertyName("height")]
    public object Height { get; set; } = "parentHeight";
}

public class RemarkableRepeat
{
    [JsonPropertyName("columns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Columns { get; set; }

    [JsonPropertyName("rows")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Rows { get; set; }
}

public class RemarkablePathItem : RemarkableItem
{
    [JsonIgnore]
    public override string Type => "path";

    [JsonPropertyName("data")]
    public List<string> Data { get; set; } = [];

    [JsonPropertyName("fillColor")]
    public string? FillColor { get; set; }

    [JsonPropertyName("strokeColor")]
    public string? StrokeColor { get; set; }

    [JsonPropertyName("strokeWidth")]
    public double StrokeWidth { get; set; } = 0;

    [JsonPropertyName("antialiasing")]
    public bool Antialiasing { get; set; } = true;
}

public record ConversionSettings(
    string TemplateName = "pictograph_master",
    string Author = "Douglas",
    string Orientation = "portrait",
    int GridCols = 3,
    int GridRows = 4,
    bool UseParametricScaling = true,
    int DecimalPrecision = 4,
    bool SnapToEinkGrayscale = false,
    double DefaultStrokeWidth = 1.0,
    bool ExportFullColor = true // Preserves the 9 reMarkable exported colors
);
