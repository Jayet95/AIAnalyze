namespace AIAnalyze.Desktop;

internal static class Program
{
    /// <summary>
    /// WinForms and WebView2 need a single-threaded apartment; top-level statements don't set one, which made
    /// WebView2 fail with RPC_E_CHANGED_MODE.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--enable-startup"))
        {
            Startup.Enable();
        }
        if (args.Contains("--disable-startup"))
        {
            Startup.Disable();
            return;
        }

        // One window per user: a second launch asks the running one to show itself, then exits.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AIAnalyze.Desktop", out var firstInstance);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIAnalyze.Desktop.Show");
        if (!firstInstance)
        {
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(startHidden: args.Contains("--background"), showSignal));
    }
}

/// <summary>Starting with Windows, for the current user (HKCU Run key).</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIAnalyze";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
    }

    public static void Disable()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
