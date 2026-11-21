using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.Text.Json;

namespace AIAnalyze.Desktop;

/// <summary>
/// Lives in the notification area for as long as the user is signed in to Windows. Owns the main window
/// (created on demand), shows Windows notifications for new alerts, and offers Open, Check health now,
/// Start with Windows and Quit.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    public static readonly Uri ServiceUrl = new("http://127.0.0.1:5280/");

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _startupItem;
    private readonly HttpClient _http = new() { BaseAddress = ServiceUrl, Timeout = TimeSpan.FromSeconds(8) };
    private readonly System.Windows.Forms.Timer _alertTimer = new() { Interval = 30_000 };
    private readonly SynchronizationContext _ui;
    private MainWindow? _window;
    private long? _lastAlertId;
    private bool _toldAboutTray;

    public TrayApp(bool startHidden, EventWaitHandle showSignal)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = Startup.IsEnabled() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open AIAnalyze", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add("Check health now", null, async (_, _) => await CheckHealthNowAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "AIAnalyze",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => ShowWindow();

        // A second launch of the app signals this one to show its window.
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => _ui.Post(_ => ShowWindow(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        _alertTimer.Tick += async (_, _) => await PollAlertsAsync();
        _alertTimer.Start();
        _ = PollAlertsAsync();

        if (!startHidden)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainWindow(this);
            _window.FormClosing += (_, e) =>
            {
                // Closing the window keeps AIAnalyze running in the notification area.
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _window.Hide();
                    if (!_toldAboutTray)
                    {
                        _toldAboutTray = true;
                        _tray.ShowBalloonTip(5000, "AIAnalyze is still running",
                            "It keeps watching your PC from the notification area. Right-click the icon to quit.", ToolTipIcon.Info);
                    }
                }
            };
        }
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = FormWindowState.Normal;
        }
        _window.Activate();
    }

    /// <summary>Shows a Windows notification for alerts that arrived since the last check.</summary>
    private async Task PollAlertsAsync()
    {
        try
        {
            var alerts = await _http.GetFromJsonAsync<List<Alert>>($"api/alerts?after={_lastAlertId ?? 0}") ?? [];
            if (alerts.Count == 0)
            {
                _lastAlertId ??= 0;
                return;
            }

            var firstCheck = _lastAlertId is null;
            _lastAlertId = alerts.Max(a => a.Id);
            if (firstCheck)
            {
                return; // only alerts that arrive while the app runs are announced
            }

            var newest = alerts.OrderByDescending(a => a.Id).First();
            var title = alerts.Count == 1 ? AlertTitle(newest) : $"AIAnalyze: {alerts.Count} new alerts";
            var icon = newest.Severity is "Critical" ? ToolTipIcon.Error : newest.Severity is "Warning" ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _tray.ShowBalloonTip(10_000, title, Shorten(newest.Message, 200), icon);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // The service isn't reachable right now; the window shows that state.
        }
    }

    private async Task CheckHealthNowAsync()
    {
        try
        {
            using var response = await _http.PostAsync("api/health/run", JsonContent.Create(new { }));
            _tray.ShowBalloonTip(4000, "Checking your PC", "The results appear on the Health page in a few seconds.", ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _tray.ShowBalloonTip(5000, "AIAnalyze service isn't running", "Start it from Services, or reinstall AIAnalyze.", ToolTipIcon.Warning);
        }
    }

    private void ToggleStartup()
    {
        if (Startup.IsEnabled())
        {
            Startup.Disable();
        }
        else
        {
            Startup.Enable();
        }
        _startupItem.Checked = Startup.IsEnabled();
    }

    public void Quit()
    {
        _alertTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _window?.Dispose();
        _http.Dispose();
        ExitThread();
    }

    private static string AlertTitle(Alert a) => a.Kind switch
    {
        "Feedback" => "AIAnalyze reviewed a session",
        "Temperature" => "AIAnalyze: running hot",
        _ when a.Severity == "Critical" => "AIAnalyze: problem found",
        _ => "AIAnalyze: something to look at",
    };

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private sealed record Alert(long Id, string Kind, string Severity, string Message);
}

/// <summary>The app icon, drawn in code so no image file is needed: a rounded square with a pulse line.</summary>
internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var background = RoundedRect(new Rectangle(2, 2, 60, 60), 16);
            using var fill = new SolidBrush(Color.FromArgb(59, 108, 246));
            g.FillPath(fill, background);
            using var pen = new Pen(Color.White, 6) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [new Point(12, 40), new Point(23, 27), new Point(32, 36), new Point(42, 19), new Point(52, 30)]);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
