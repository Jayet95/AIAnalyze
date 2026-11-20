namespace AIAnalyze.Analysis;

/// <summary>One frame as reported by PresentMon's CSV output.</summary>
public sealed record FrameRecord(string Application, double TimeSeconds, double MsBetweenPresents);

/// <summary>
/// Parses a complete PresentMon CSV output in one call. Uses the same rules as
/// <see cref="PresentMonLineParser"/>: lines before the header and bad rows are skipped.
/// If no header is found, the result is empty.
/// </summary>
public static class PresentMonParser
{
    public static IReadOnlyList<FrameRecord> Parse(IEnumerable<string> lines)
    {
        var parser = new PresentMonLineParser();
        var frames = new List<FrameRecord>();

        foreach (var line in lines)
        {
            if (parser.TryParse(line, out var frame) && frame is not null)
            {
                frames.Add(frame);
            }
        }

        return frames;
    }
}
