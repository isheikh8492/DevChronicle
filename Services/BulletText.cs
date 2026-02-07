using System;
using System.Collections.Generic;
using System.Linq;

namespace DevChronicle.Services;

public static class BulletText
{
    public static List<string> NormalizeToDashBullets(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var lines = text
            .Split('\n', StringSplitOptions.None)
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var bullets = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                bullets.Add(line);
                continue;
            }

            if (line.StartsWith("•", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
            {
                var remainder = line.Length > 1 ? line.Substring(1).TrimStart() : string.Empty;
                bullets.Add(string.IsNullOrWhiteSpace(remainder) ? "- " : $"- {remainder}");
                continue;
            }

            bullets.Add($"- {line}");
        }

        return bullets;
    }
}

