using System.Windows.Media;

namespace SshIt.Services;

/// <summary>
/// Colour tags: a session's own colour, so production and lab are distinguishable at a glance in the
/// tree and on the tab strip. Stored on the Session as "#RRGGBB"; anything else is no tag rather than
/// a crash, because the value travels through a JSON file a person can edit.
/// </summary>
public static class SessionTag
{
    /// <summary>The picker's palette. Muted enough to sit on the dark theme without glowing.</summary>
    public static readonly string[] Presets =
    {
        "#E05252", // red
        "#E08A3C", // orange
        "#D9B23C", // yellow
        "#4CAF6E", // green
        "#3FB6B0", // teal
        "#4A8FE0", // blue
        "#9A6FE0", // purple
        "#D96FA8"  // pink
    };

    /// <summary>"#RRGGBB" in upper case, or null. A bare "ff8800" is accepted; junk is not.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return null;
        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i]))
                return null;
        return text.ToUpperInvariant();
    }

    /// <summary>A word for the colour, so a tooltip and a screen reader say something useful.</summary>
    public static string Name(string? value) => Normalize(value) switch
    {
        "#E05252" => "Red",
        "#E08A3C" => "Orange",
        "#D9B23C" => "Yellow",
        "#4CAF6E" => "Green",
        "#3FB6B0" => "Teal",
        "#4A8FE0" => "Blue",
        "#9A6FE0" => "Purple",
        "#D96FA8" => "Pink",
        null => "No colour",
        _ => "Custom colour"
    };

    /// <summary>A frozen brush for the tag, or null when there is no usable colour.</summary>
    public static Brush? Brush(string? value)
    {
        var hex = Normalize(value);
        if (hex is null) return null;
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
