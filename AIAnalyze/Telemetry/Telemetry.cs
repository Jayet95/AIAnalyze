using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AIAnalyze.Account;
using AIAnalyze.Capture;
using AIAnalyze.Health;
using AIAnalyze.Storage;

namespace AIAnalyze.Telemetry;

/// <summary>
/// What an opt-in snapshot contains. Deliberately no program, file, user or computer names, no paths,
/// no Event Log text, and no chat content: hardware models, driver versions, which checks failed, which
/// finding kinds appeared, and how games performed.
/// </summary>
public sealed record TelemetrySnapshot(
    int Schema,
    string AppVersion,
    string WindowsBuild,
    string Cpu,
    IReadOnlyList<TelemetryGpu> Gpus,
    int RamGb,
    IReadOnlyList<TelemetryCheck> Health,
    IReadOnlyDictionary<string, int> Findings7d,
    IReadOnlyList<TelemetryGame> Games7d);

public sealed record TelemetryGpu(string Name, string Driver);

public sealed record TelemetryCheck(string Id, string? Severity);

/// <summary>A game's executable name (for example cs2.exe), its sessions in the last 7 days, and their median FPS.</summary>
public sealed record TelemetryGame(string Game, int Sessions, double MedianFps, int Minutes);

public sealed record FeedbackVote(string Target, string Kind, bool Helpful, DateTimeOffset AtUtc);

public static class TelemetryBuilder
{
    public static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static TelemetrySnapshot Build(SampleStore store, HealthReport? health, DateTimeOffset now)
    {
        var specs = store.GetLatestSpecs();
        var since = now.AddDays(-7);
        var sessions = store.ListSessions(1000).Where(s => s.StartUtc >= since).ToList();

        var findings = sessions
            .SelectMany(s => store.GetFindings(s.Id))
            .GroupBy(f => f.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        var games = sessions
            .GroupBy(s => s.Application.ToLowerInvariant())
            .Select(g => new TelemetryGame(
                g.Key,
                g.Count(),
                Math.Round(Analysis.Stats.Median(g.Select(s => s.MedianFps).ToList()), 1),
                (int)g.Sum(s => (s.EndUtc - s.StartUtc).TotalMinutes)))
            .OrderByDescending(g => g.Minutes)
            .Take(20)
            .ToList();

        return new TelemetrySnapshot(
            Schema: 1,
            AppVersion: AppVersion,
            WindowsBuild: specs?.OsBuild ?? "",
            Cpu: specs?.CpuName ?? "",
            Gpus: specs?.Gpus.Select(g => new TelemetryGpu(g.Name, g.DriverVersion)).ToList() ?? [],
            RamGb: (int)Math.Round(specs?.RamTotalGb ?? 0),
            Health: health?.Checks.Select(c => new TelemetryCheck(c.Id, c.Severity?.ToString())).ToList() ?? [],
            Findings7d: findings,
            Games7d: games);
    }
}

/// <summary>The user's sharing choice and this install's random device id.</summary>
public sealed class PrivacySettings
{
    private const string ConsentKey = "sharing_consent";
    private const string DeviceKey = "device_id";

    /// <summary>True when the user agreed, false when they declined, null when not asked yet.</summary>
    public bool? Consent
    {
        get
        {
            using var store = Open();
            return store.GetSetting(ConsentKey) switch { "granted" => true, "denied" => false, _ => null };
        }
    }

    public void SetConsent(bool share)
    {
        using var store = Open();
        store.SetSetting(ConsentKey, share ? "granted" : "denied");
    }

    /// <summary>A random id created on first use. It identifies this install, not the hardware or the person.</summary>
    public string DeviceId
    {
        get
        {
            using var store = Open();
            var id = store.GetSetting(DeviceKey);
            if (id is null)
            {
                id = Guid.NewGuid().ToString();
                store.SetSetting(DeviceKey, id);
            }
            return id;
        }
    }

    private static SampleStore Open() => new(TelemetryPaths.DatabasePath);
}

/// <summary>
/// Uploads a daily snapshot and queued feedback votes, only when the user agreed to share and a server
/// is configured. Signed-in uploads carry the account; otherwise they are tied only to the random device id.
/// </summary>
public sealed class TelemetryService(
    ILogger<TelemetryService> logger,
    PrivacySettings privacy,
    CloudOptions cloud,
    AccountService account,
    HealthState health,
    IHttpClientFactory httpFactory) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await UploadOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogInformation(ex, "Diagnostics upload skipped; it will retry later.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Service stopping.
        }
    }

    public async Task UploadOnceAsync(CancellationToken cancellationToken)
    {
        if (privacy.Consent != true || !cloud.ApiConfigured)
        {
            return;
        }

        using var store = new SampleStore(TelemetryPaths.DatabasePath);
        var now = DateTimeOffset.UtcNow;
        var lastSnapshot = long.TryParse(store.GetSetting("last_snapshot_utc"), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;
        var snapshot = now - lastSnapshot >= SnapshotEvery ? TelemetryBuilder.Build(store, health.Latest, now) : null;
        var pending = store.GetPendingOutbox("feedback", 200);
        if (snapshot is null && pending.Count == 0)
        {
            return;
        }

        var feedback = pending.Select(p => JsonSerializer.Deserialize<FeedbackVote>(p.Payload, Json)).Where(v => v is not null).ToList();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{cloud.ApiBaseUrl.TrimEnd('/')}/v1/telemetry")
        {
            Content = JsonContent.Create(new { deviceId = privacy.DeviceId, snapshot, feedback }, options: Json),
        };
        if (await account.GetAccessTokenAsync(cancellationToken) is { } token)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        using var response = await httpFactory.CreateClient("cloud").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Diagnostics upload was not accepted ({Status}); it will retry later.", (int)response.StatusCode);
            return;
        }

        store.MarkOutboxSent(pending.Select(p => p.Id), now);
        if (snapshot is not null)
        {
            store.SetSetting("last_snapshot_utc", now.ToUnixTimeMilliseconds().ToString());
        }
    }
}
