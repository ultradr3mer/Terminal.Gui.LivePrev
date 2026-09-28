using System;
using System.Collections.Generic;
using System.Text;

namespace TuiPreview.Host.Preview
{
  using Microsoft.CodeAnalysis;
  using System.Globalization;

  internal sealed record SeverityStyle(DiagnosticSeverity Severity, string Singular, string Plural, string Icon);

  internal static class DiagnosticSummary
  {
    public static TimeSpan GetAge(DateTime timestamp)
    {
      return DateTime.UtcNow - timestamp.ToUniversalTime();
    }
    /// <summary>"3 new" / "3 ✲", "3 a moment ago" / "3 ⏱", "3 5m ago" / "3 ⧗5m", "14:05 - 3" / "3 @14:05".</summary>
    public static (string Long, string Short) ToHumanStatus(DateTime timestamp, int entries)
    {
      var age = GetAge(timestamp);
      var local = timestamp.ToLocalTime();

      string entriesStr = entries == 0 ? "no entries" : $"[+{entries}]";
      string entriesShort = $"[+{(entries == 0 ? "∅" : entries.ToString())}]";

      return age.TotalMinutes < 1
          ? (entries == 0 ? "no new entries" : $"{entriesStr} new", $"{entriesShort}✲")
          : age.TotalMinutes < 2
              ? ($"{entriesStr} a moment ago", $"{entriesShort} ⏱")
              : age.TotalMinutes < 60
                  ? ($"{entriesStr} - {(int)age.TotalMinutes}m ago", $"{entriesShort}⧗{(int)age.TotalMinutes}m")
                  : age.TotalHours < 24
                      ? ($"{entriesStr} - {local:t}", $"{entriesShort}@{local:t}")
                      : ($"{entriesStr} - {local:d}", $"{entriesShort}@{local:d}");
    }

    static string Word((SeverityStyle Style, int Count) p) => p.Count == 1 ? p.Style.Singular : p.Style.Plural;
    // Counts visible characters, so icons made of several chars still count as one
    private static int VisibleLength(string s) => new StringInfo(s).LengthInTextElements;

    public static readonly SeverityStyle[] DefaultStyles =
    {
      new(DiagnosticSeverity.Error,   "error",   "errors",   "✖"),
      new(DiagnosticSeverity.Warning, "warning", "warnings", "⚠"),
      new(DiagnosticSeverity.Info,    "info",    "infos",    "ℹ"),
    };

    /// <summary>
    /// Builds a status message that shrinks to fit <paramref name="maxLength"/>:
    /// "3 new, 2 errors, 1 warning" → "3 ✲, 2 errors, 1 warning" → "3 ✲  ✖ 2  ⚠ 1" → "3 ✲ ✖2 ⚠1"
    /// </summary>
    public static string Format(
      int segmentEntries,
      IReadOnlyDictionary<DiagnosticSeverity, int> counts,
      DateTime lastEntryTime,
      int maxLength = 40,
      IReadOnlyList<SeverityStyle>? styles = null)
    {
      var hasEntries = segmentEntries > 0;
      var (entriesInfo, entriesShort) = ToHumanStatus(lastEntryTime, segmentEntries);
      if (!hasEntries) // Dann gibt es auch keine Fehler
      {
        return VisibleLength(entriesInfo) <= maxLength ? entriesInfo : entriesShort;
      }

      styles ??= DefaultStyles;

      var parts = styles
        .Select(s => (Style: s, Count: counts.TryGetValue(s.Severity, out var n) ? n : 0))
        .Where(p => p.Count > 0)
        .ToList();

      string[] candidates = parts.Count == 0
        ?
          [
            $"{entriesInfo}, no issues",
            $"{entriesShort} ✓",
          ]
        :
          [
            // 3 new, 2 errors, 1 warning
            string.Join(", ", parts.Select(p => $"{p.Count} {Word(p)}").Prepend(entriesInfo)),
            // 3 ✲, 2 errors, 1 warning
            string.Join(", ", parts.Select(p => $"{p.Count} {Word(p)}").Prepend(entriesInfo)),
            // 3 ✲  ✖ 2  ⚠ 1
            string.Join("  ", parts.Select(p => $"{p.Style.Icon} {p.Count}").Prepend(entriesShort)),
            // 3 ✲ ✖2 ⚠1
            string.Join(" ", parts.Select(p => $"{p.Style.Icon}{p.Count}").Prepend(entriesShort)),
          ];

      // Fall back to the most compact form, since shortening it further would drop information
      return candidates.FirstOrDefault(c => VisibleLength(c) <= maxLength) ?? candidates[^1];
    }
  }
}
