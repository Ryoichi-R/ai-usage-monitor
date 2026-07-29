using System.IO;

namespace AiUsageMonitor.App;

internal static class MarkdownSectionReader
{
    public static string Read(string markdown, string headingText)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentException.ThrowIfNullOrWhiteSpace(headingText);

        string normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var section = new List<string>();
        int targetLevel = 0;
        bool inFence = false;
        bool found = false;

        foreach (string line in lines)
        {
            if (!inFence && TryReadHeading(line, out int level, out string text))
            {
                if (found && level <= targetLevel)
                    break;

                if (!found && string.Equals(text, headingText, StringComparison.Ordinal))
                {
                    found = true;
                    targetLevel = level;
                }
            }

            if (found)
                section.Add(line);

            if (IsFence(line))
                inFence = !inFence;
        }

        if (!found)
            throw new InvalidDataException($"READMEに見出し「{headingText}」が見つかりません。");

        return string.Join(Environment.NewLine, section).TrimEnd();
    }

    private static bool TryReadHeading(string line, out int level, out string text)
    {
        string trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && trimmed[level] == '#')
            level++;

        if (level is < 1 or > 6 ||
            level >= trimmed.Length ||
            !char.IsWhiteSpace(trimmed[level]))
        {
            text = string.Empty;
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return true;
    }

    private static bool IsFence(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) ||
            trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }
}
