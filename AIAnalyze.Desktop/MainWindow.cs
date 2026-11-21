using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIAnalyze.Desktop;

/// <summary>
/// The AIAnalyze window: the service's UI in WebView2 (Edge's engine, part of Windows), without a browser frame.
/// Pages on other sites, and sign-in, open in the user's default browser, as recommended for desktop apps.
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 18, 24) };
    private readonly TrayApp _app;
    private readonly System.Windows.Forms.Timer _signInWatch = new() { Interval = 3000 };
    private DateTime _signInStarted;

    public MainWindow(TrayApp app)
    {
        _app = app;
        Text = "AIAnalyze";
        Icon = AppIcon.Create();
        MinimumSize = new Size(900, 600);
        Size = new Size(1280, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 18, 24);
        Controls.Add(_web);

        _signInWatch.Tick += async (_, _) => await CheckSignInAsync();
        Load += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                Log.Write($"Window failed to start: {ex}");
                ShowMessage("AIAnalyze could not open its window",
                    $"{ex.GetType().Name}: {ex.Message}. You can use AIAnalyze in your browser meanwhile.", showRetry: false);
            }
        };
    }

    private async Task InitializeAsync()
    {
        CoreWebView2Environment environment;
        try
        {
            // Per-user browser data for this app only (cookies, cache), separate from the user's own browser.
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIAnalyze", "WebView2");
            environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowMessage("Microsoft Edge WebView2 is missing",
                "AIAnalyze shows its window with WebView2, which is part of Windows 11 and most Windows 10 PCs. Install the free \"Evergreen\" WebView2 Runtime from Microsoft, then open AIAnalyze again. Meanwhile you can use AIAnalyze in your browser.",
                showRetry: false);
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenOutside(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && IsServicePage(core.Source))
            {
                ShowMessage("AIAnalyze service isn't running",
                    "The window shows what the AIAnalyze service measures, but the service isn't answering. It starts with Windows; if it doesn't, start \"AIAnalyze PC Diagnostics\" in Services or reinstall AIAnalyze.",
                    showRetry: true);
            }
        };
        core.WebMessageReceived += (_, e) =>
        {
            var message = e.TryGetWebMessageAsString();
            if (message == "retry")
            {
                core.Navigate(TrayApp.ServiceUrl.ToString());
            }
            else if (message == "browser")
            {
                OpenOutside(TrayApp.ServiceUrl.ToString());
            }
        };

        Log.Write($"WebView2 {environment.BrowserVersionString} ready; opening {TrayApp.ServiceUrl}");
        core.Navigate(TrayApp.ServiceUrl.ToString());
    }

    /// <summary>The app's own pages stay in the window; sign-in and other sites open in the default browser.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme is "data" or "about")
        {
            return;
        }

        if (IsServicePage(e.Uri) && uri.AbsolutePath.StartsWith("/auth/sign-in", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            OpenOutside(e.Uri);
            _signInStarted = DateTime.UtcNow;
            _signInWatch.Start();
            return;
        }

        if (!IsServicePage(e.Uri))
        {
            e.Cancel = true;
            OpenOutside(e.Uri);
        }
    }

    /// <summary>After sign-in starts in the browser, reload the window once the service reports the account.</summary>
    private async Task CheckSignInAsync()
    {
        if (DateTime.UtcNow - _signInStarted > TimeSpan.FromMinutes(5))
        {
            _signInWatch.Stop();
            return;
        }
        try
        {
            using var http = new HttpClient { BaseAddress = TrayApp.ServiceUrl, Timeout = TimeSpan.FromSeconds(5) };
            var account = await http.GetFromJsonAsync<AccountStatus>("api/account");
            if (account?.SignedIn == true)
            {
                _signInWatch.Stop();
                _web.CoreWebView2?.Navigate(new Uri(TrayApp.ServiceUrl, "/#signed-in").ToString());
                Activate();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }
    }

    private void ShowMessage(string title, string body, bool showRetry)
    {
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
            body{margin:0;height:100vh;display:grid;place-items:center;background:#0f1218;color:#e8ebf2;font:15px 'Segoe UI',system-ui}
            .box{max-width:520px;padding:24px} h2{margin:0 0 10px} p{color:#a3abbd;line-height:1.55}
            button{margin-right:8px;padding:8px 16px;border-radius:8px;border:0;font:inherit;font-weight:600;cursor:pointer}
            .primary{background:#6d93ff;color:#fff} .secondary{background:#1e232d;color:#e8ebf2}
            </style></head><body><div class="box"><h2>{{System.Net.WebUtility.HtmlEncode(title)}}</h2>
            <p>{{System.Net.WebUtility.HtmlEncode(body)}}</p>
            {{(showRetry ? "<button class=\"primary\" onclick=\"chrome.webview.postMessage('retry')\">Try again</button>" : "")}}
            <button class="secondary" onclick="chrome.webview.postMessage('browser')">Open in browser</button>
            </div></body></html>
            """;
        if (_web.CoreWebView2 is { } core)
        {
            core.NavigateToString(html);
        }
        else
        {
            MessageBox.Show(this, body, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static bool IsServicePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "http"
        && uri.Host is "127.0.0.1" or "localhost"
        && uri.Port == TrayApp.ServiceUrl.Port;

    private static void OpenOutside(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _signInWatch.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record AccountStatus(bool SignedIn);
}

/// <summary>A small per-user log for the window app: %LocalAppData%\AIAnalyze\desktop.log.</summary>
internal static class Log
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIAnalyze", "desktop.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
            {
                File.Delete(FilePath);
            }
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
