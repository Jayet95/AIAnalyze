namespace AIAnalyze.Analysis;

/// <summary>One frame as reported by PresentMon's CSV output.</summary>
public sealed record FrameRecord(string Application, double TimeSeconds, double MsBetweenPresents);

/// <summary>
/// Parses PresentMon CSV output. Columns are found by header name, so the parser
/// does not depend on column order. Rows with a missing or non-numeric value are skipped.
/// </summary>
public static class PresentMonParser
{
    public static IReadOnlyList<FrameRecord> Parse(IEnumerable<string> lines)
    {
        var frames = new List<FrameRecord>();
        int? appIdx = null, timeIdx = null, frameTimeIdx = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var cols = line.Split(',');

            if (appIdx is null)
            {
                // First non-empty line is the header.
                appIdx = Array.IndexOf(cols, "Application");
                timeIdx = Array.IndexOf(cols, "TimeInSeconds");
                frameTimeIdx = Array.IndexOf(cols, "MsBetweenPresents");
                if (appIdx < 0 || timeIdx < 0 || frameTimeIdx < 0)
                {
                    throw new FormatException(
                        "PresentMon header is missing Application, TimeInSeconds or MsBetweenPresents.");
                }
                continue;
            }

            if (cols.Length <= Math.Max(appIdx.Value, Math.Max(timeIdx!.Value, frameTimeIdx!.Value)))
            {
                continue;
            }

            if (double.TryParse(cols[timeIdx.Value], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var time) &&
                double.TryParse(cols[frameTimeIdx.Value], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms) &&
                ms > 0)
            {
                frames.Add(new FrameRecord(cols[appIdx.Value].Trim('"'), time, ms));
            }
        }

        return frames;
    }
}
