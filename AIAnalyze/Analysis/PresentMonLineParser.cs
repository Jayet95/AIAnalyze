using System.Globalization;

namespace AIAnalyze.Analysis;

/// <summary>
/// Streaming parser for PresentMon CSV output. The header is the first line that contains
/// Application, TimeInSeconds and MsBetweenPresents (matched case-insensitively). Lines before
/// the header, such as status messages, are skipped. Nothing here throws on bad input.
/// </summary>
public sealed class PresentMonLineParser
{
    private int? _appIdx;
    private int _timeIdx;
    private int _frameTimeIdx;

    /// <summary>True once a header line has been recognised.</summary>
    public bool HeaderFound => _appIdx is not null;

    /// <summary>Returns true and the frame for a valid data row. Header, pre-header and bad rows return false.</summary>
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
            TryReadHeader(cols);
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
            frame = new FrameRecord(cols[_appIdx.Value].Trim().Trim('"'), seconds, msBetween);
            return true;
        }

        return false;
    }

    private void TryReadHeader(string[] cols)
    {
        var app = IndexOf(cols, "Application");
        var time = IndexOf(cols, "TimeInSeconds");
        var ms = IndexOf(cols, "MsBetweenPresents");
        if (app < 0 || time < 0 || ms < 0)
        {
            return;
        }

        _appIdx = app;
        _timeIdx = time;
        _frameTimeIdx = ms;
    }

    private static int IndexOf(string[] cols, string name) =>
        Array.FindIndex(cols, c => string.Equals(c.Trim().Trim('"'), name, StringComparison.OrdinalIgnoreCase));
}
