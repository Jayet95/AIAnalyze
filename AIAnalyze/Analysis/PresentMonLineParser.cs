using System.Globalization;

namespace AIAnalyze.Analysis;

/// <summary>
/// Streaming parser for PresentMon CSV output: the first non-empty line is the header,
/// every later line is parsed on its own. Columns are located by header name.
/// </summary>
public sealed class PresentMonLineParser
{
    private int? _appIdx;
    private int _timeIdx;
    private int _frameTimeIdx;

    /// <summary>Returns true and the frame for a valid data row. Header and bad rows return false.</summary>
    public bool TryParse(string raw, out FrameRecord? frame)
    {
        frame = null;
        var line = raw.Trim();
        if (line.Length == 0)
        {
            return false;
        }

        var cols = line.Split(',');

        if (_appIdx is null)
        {
            var app = Array.IndexOf(cols, "Application");
            var time = Array.IndexOf(cols, "TimeInSeconds");
            var ms = Array.IndexOf(cols, "MsBetweenPresents");
            if (app < 0 || time < 0 || ms < 0)
            {
                throw new FormatException(
                    "PresentMon header is missing Application, TimeInSeconds or MsBetweenPresents.");
            }
            _appIdx = app;
            _timeIdx = time;
            _frameTimeIdx = ms;
            return false;
        }

        var needed = Math.Max(_appIdx.Value, Math.Max(_timeIdx, _frameTimeIdx));
        if (cols.Length <= needed)
        {
            return false;
        }

        if (double.TryParse(cols[_timeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            double.TryParse(cols[_frameTimeIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var msBetween) &&
            msBetween > 0)
        {
            frame = new FrameRecord(cols[_appIdx.Value].Trim('"'), seconds, msBetween);
            return true;
        }

        return false;
    }
}
