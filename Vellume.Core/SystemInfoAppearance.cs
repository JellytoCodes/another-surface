using System.Text.Json.Serialization;

namespace Vellume.Core;

public sealed record SystemInfoAppearance
{
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowDriveC { get; set; } = true;
    public bool ShowDriveD { get; set; } = true;
    public string AccentColor { get; set; } = "#1696F3";
    public string TextColor { get; set; } = "#FFFFFF";
    public double Scale { get; set; } = 1;
    public double BackgroundOpacity { get; set; } = 0.75;

    [JsonIgnore]
    public bool HasVisibleItems => ShowCpu || ShowGpu || ShowMemory || ShowDriveC || ShowDriveD;

    public SystemInfoAppearance Copy() => this with
    {
        AccentColor = NormalizeColor(AccentColor, "#1696F3"),
        TextColor = NormalizeColor(TextColor, "#FFFFFF"),
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.5, 2) : 1,
        BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : 0.75
    };

    private static string NormalizeColor(string? value, string fallback)
    {
        var hex = value?.Trim().TrimStart('#');
        return hex is { Length: 6 } && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : fallback;
    }
}
