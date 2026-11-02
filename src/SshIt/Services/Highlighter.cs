using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SshIt.Models;

namespace SshIt.Services;

/// <summary>
/// Keyword highlighting: finds the character spans one visible terminal row should paint with a
/// rule's wash or colour. Pure and independent of WPF, so the matching rules are testable without
/// a window.
///
/// Rules are applied in order and the first rule to claim a character owns it, which makes the
/// order in the settings list meaningful (put the specific rule above the general one).
/// </summary>
public static class Highlighter
{
    /// <summary>One painted span. <see cref="Start"/> and <see cref="Length"/> are character offsets.</summary>
    public readonly record struct Span(int Start, int Length, HighlightRule Rule);

    private const int MaxSpansPerLine = 500;

    // Compiling a Regex per row per frame is wasteful; a failed compile is cached as null so a
    // broken pattern costs one attempt, not one per frame.
    private static readonly ConcurrentDictionary<(string Pattern, bool IgnoreCase), Regex?> Cache = new();

    public static IReadOnlyList<Span> Spans(string? text, IReadOnlyList<HighlightRule>? rules)
    {
        if (string.IsNullOrEmpty(text) || rules is null || rules.Count == 0) return Array.Empty<Span>();

        List<Span>? spans = null;
        foreach (var rule in rules)
        {
            if (rule is null || !rule.Enabled) continue;
            var pattern = rule.Pattern;
            if (string.IsNullOrEmpty(pattern)) continue;

            if (rule.IsRegex)
            {
                var regex = Compile(pattern, rule.IgnoreCase);
                if (regex is null) continue;
                try
                {
                    foreach (Match m in regex.Matches(text))
                    {
                        if (m.Length <= 0) continue;
                        (spans ??= new List<Span>()).Add(new Span(m.Index, m.Length, rule));
                        if (spans.Count >= MaxSpansPerLine) return spans;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern must never freeze the renderer.
                }
                continue;
            }

            var comparison = rule.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var at = 0;
            while (at <= text.Length - pattern.Length)
            {
                var hit = text.IndexOf(pattern, at, comparison);
                if (hit < 0) break;
                (spans ??= new List<Span>()).Add(new Span(hit, pattern.Length, rule));
                if (spans.Count >= MaxSpansPerLine) return spans;
                at = hit + Math.Max(1, pattern.Length);
            }
        }

        return spans ?? (IReadOnlyList<Span>)Array.Empty<Span>();
    }

    /// <summary>Returns the compiled regex, or null when the pattern is not valid.</summary>
    public static Regex? Compile(string pattern, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        return Cache.GetOrAdd((pattern, ignoreCase), key =>
        {
            try
            {
                var options = RegexOptions.CultureInvariant;
                if (key.IgnoreCase) options |= RegexOptions.IgnoreCase;
                return new Regex(key.Pattern, options, TimeSpan.FromMilliseconds(120));
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Builds a per-character mask of rule indexes (-1 = no rule) with first-rule-wins semantics,
    /// which is what the renderer needs to colour a row without repainting claimed characters.
    /// </summary>
    public static int[] Mask(string? text, IReadOnlyList<HighlightRule>? rules)
    {
        var length = text?.Length ?? 0;
        var mask = new int[length];
        if (length == 0) return mask;
        Array.Fill(mask, -1);

        var spans = Spans(text, rules);
        if (spans.Count == 0) return mask;

        // Rule identity -> index in the caller's list, so the mask is usable for lookups.
        // HighlightRule has no Equals override, so dictionary identity is reference identity.
        var order = new Dictionary<HighlightRule, int>();
        if (rules is not null)
            for (var i = 0; i < rules.Count; i++) order[rules[i]] = i;

        foreach (var span in spans)
        {
            var id = order.TryGetValue(span.Rule, out var index) ? index : 0;
            for (var i = span.Start; i < span.Start + span.Length && i < length; i++)
                if (mask[i] < 0) mask[i] = id;
        }
        return mask;
    }

    /// <summary>Parses "#RRGGBB" into a colour; null when the string is empty or malformed.</summary>
    public static System.Windows.Media.Color? ParseColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var text = hex.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(text);
        }
        catch
        {
            return null;
        }
    }
}
