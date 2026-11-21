using System.Net;
using System.Text;
using System.Text.Json;
using AIAnalyze.Account;
using AIAnalyze.Analysis;
using AIAnalyze.Chat;
using AIAnalyze.Health;
using AIAnalyze.Storage;
using AIAnalyze.Telemetry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIAnalyze.Tests;

/// <summary>A fake OpenID Connect provider: discovery, token, userinfo and revocation endpoints.</summary>
internal sealed class FakeProvider : HttpMessageHandler
{
    public const string Issuer = "https://id.example.test";
    public List<Dictionary<string, string>> TokenRequests { get; } = [];
    public int ExpiresIn { get; set; } = 3600;
    private int _issued;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            return Json(new
            {
                authorization_endpoint = $"{Issuer}/authorize",
                token_endpoint = $"{Issuer}/token",
                userinfo_endpoint = $"{Issuer}/userinfo",
                revocation_endpoint = $"{Issuer}/revoke",
            });
        }
        if (path == "/token")
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            TokenRequests.Add(form);
            _issued++;
            return Json(new { access_token = $"access-{_issued}", refresh_token = $"refresh-{_issued}", expires_in = ExpiresIn });
        }
        if (path == "/userinfo")
        {
            return Json(new { sub = "user_123", email = "player@example.test", name = "Player One" });
        }
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

public class AccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aianalyze-account-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();
    private readonly CloudOptions _options = new() { Issuer = FakeProvider.Issuer, ClientId = "desktop-app", ApiBaseUrl = "https://api.example.test" };

    private AccountService NewService(out AccountStore store)
    {
        store = new AccountStore(Path.Combine(_dir, "account.json"), lockDown: false);
        var oidc = new OidcClient(new HttpClient(_provider), _options);
        return new AccountService(_options, oidc, store, NullLogger<AccountService>.Instance);
    }

    private static Dictionary<string, string> Query(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void Pkce_challenge_matches_the_rfc_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task Sign_in_sends_a_pkce_challenge_and_exchanges_the_code_with_its_verifier()
    {
        var service = NewService(out var store);
        var redirect = "http://127.0.0.1:5280/auth/callback";

        var url = await service.BeginSignInAsync(redirect, CancellationToken.None);
        var query = Query(url);
        Assert.StartsWith($"{FakeProvider.Issuer}/authorize?", url);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(redirect, query["redirect_uri"]);
        Assert.Equal("desktop-app", query["client_id"]);

        await service.CompleteSignInAsync(query["state"], "one-time-code", CancellationToken.None);

        var exchange = Assert.Single(_provider.TokenRequests);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("one-time-code", exchange["code"]);
        Assert.Equal(query["code_challenge"], Pkce.Challenge(exchange["code_verifier"]));
        Assert.True(service.Status.SignedIn);
        Assert.Equal("player@example.test", service.Status.Email);
        Assert.Equal("access-1", store.Load()!.AccessToken);
    }

    [Fact]
    public async Task A_callback_with_an_unknown_state_is_rejected()
    {
        var service = NewService(out _);
        await service.BeginSignInAsync("http://127.0.0.1:5280/auth/callback", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteSignInAsync("forged-state", "attacker-code", CancellationToken.None));
        Assert.Empty(_provider.TokenRequests);
        Assert.False(service.SignedIn);
    }

    [Fact]
    public async Task An_expiring_token_is_refreshed_and_sign_out_removes_it()
    {
        _provider.ExpiresIn = 30; // expires within the one-minute margin, so the next use refreshes
        var service = NewService(out var store);
        var query = Query(await service.BeginSignInAsync("http://127.0.0.1:5280/auth/callback", CancellationToken.None));
        await service.CompleteSignInAsync(query["state"], "code", CancellationToken.None);

        var token = await service.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("access-2", token);
        Assert.Equal("refresh_token", _provider.TokenRequests[1]["grant_type"]);
        Assert.Equal("refresh-1", _provider.TokenRequests[1]["refresh_token"]);

        await service.SignOutAsync(CancellationToken.None);
        Assert.False(service.SignedIn);
        Assert.Null(store.Load());
        Assert.Null(await service.GetAccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public void Chat_goes_through_the_account_when_signed_in_and_otherwise_the_own_key()
    {
        var previous = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            var signedOut = NewService(out _);
            var cloud = new CloudChat(new HttpClient(_provider), _options, signedOut);

            Assert.Equal(ChatMode.Off, new ChatRouter(new ClaudeChat(new ClaudeOptions()), cloud, new ClaudeOptions()).Mode);
            var withKey = new ClaudeOptions { ApiKey = "test-key" };
            Assert.Equal(ChatMode.ApiKey, new ChatRouter(new ClaudeChat(withKey), cloud, withKey).Mode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", previous);
        }
    }

    [Fact]
    public async Task Chat_uses_the_account_once_signed_in()
    {
        var service = NewService(out _);
        var query = Query(await service.BeginSignInAsync("http://127.0.0.1:5280/auth/callback", CancellationToken.None));
        await service.CompleteSignInAsync(query["state"], "code", CancellationToken.None);
        var withKey = new ClaudeOptions { ApiKey = "test-key" };

        var router = new ChatRouter(new ClaudeChat(withKey), new CloudChat(new HttpClient(_provider), _options, service), withKey);

        Assert.Equal(ChatMode.Account, router.Mode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}

public class TelemetryTests
{
    [Fact]
    public void Snapshot_carries_models_and_results_but_no_names_paths_or_messages()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aianalyze-telemetry-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SampleStore(path);
            var now = DateTimeOffset.UtcNow;
            store.SaveSpecsIfChanged(now, new AIAnalyze.Sensors.SystemSpecs(
                "ExampleCorp", "Model X", "Windows 11", "10.0", "26100", "AMD Ryzen 7 7800X3D", 8, 16, 4200,
                "Board", "B650", "1.0", 32, [new AIAnalyze.Sensors.GpuSpec("NVIDIA GeForce RTX 4070", "32.0.1", 12)]));
            var id = store.InsertSession(new CompletedSession("Cyberpunk2077.exe", now.AddHours(-2), now.AddHours(-1), 300000, 90, []), "default");
            store.SaveFindings(id, [new SessionFinding("GpuBound", FindingSeverity.Info, "The GPU was 98% busy")]);

            var health = new HealthReport(now, TimeSpan.FromDays(3),
                [
                    new HealthCheckResult("app-crashes", "Program crashes", FindingSeverity.Warning,
                        "SecretProject.exe crashed repeatedly", ["SecretProject.exe: 4 crash(es)", @"C:\Users\someone\Documents"]),
                ],
                null, [], [new ProcessUse("SecretProject", 1, 50, 100)], []);

            var snapshot = TelemetryBuilder.Build(store, health, now);
            var json = JsonSerializer.Serialize(snapshot);

            Assert.Equal("26100", snapshot.WindowsBuild);
            Assert.Equal("32.0.1", Assert.Single(snapshot.Gpus).Driver);
            Assert.Equal(new TelemetryCheck("app-crashes", "Warning"), Assert.Single(snapshot.Health));
            Assert.Equal(1, snapshot.Findings7d["GpuBound"]);
            Assert.Equal("cyberpunk2077.exe", Assert.Single(snapshot.Games7d).Game);
            Assert.DoesNotContain("SecretProject", json);
            Assert.DoesNotContain("someone", json);
            Assert.DoesNotContain("98% busy", json);
            Assert.DoesNotContain("ExampleCorp", json);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
