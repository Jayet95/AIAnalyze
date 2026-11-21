using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AIAnalyze.Account;

/// <summary>The signed-in account as stored on this PC.</summary>
public sealed record AccountTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string? Subject,
    string? Email,
    string? Name);

/// <summary>
/// Stores the account tokens in a file only SYSTEM and Administrators can read (the service runs as SYSTEM),
/// the same protection as the API key file.
/// </summary>
public sealed class AccountStore(string path, bool lockDown)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AccountTokens? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AccountTokens>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(AccountTokens tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path))
        {
            // Create and lock the file before any token is written to it.
            File.WriteAllText(path, "{}");
            if (lockDown && OperatingSystem.IsWindows())
            {
                LockDown(path);
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(tokens, Json));
    }

    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void LockDown(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        new FileInfo(file).SetAccessControl(security);
    }
}

public sealed record AccountStatus(bool SignInConfigured, bool SignedIn, string? Email, string? Name);

/// <summary>
/// Sign-in for the desktop app. The browser does the sign-in at the provider; the provider redirects to
/// the app's loopback address with a one-time code, which is exchanged together with the PKCE verifier.
/// The app never sees the password. Access tokens are refreshed shortly before they expire.
/// </summary>
public sealed class AccountService(CloudOptions options, OidcClient oidc, AccountStore store, ILogger<AccountService> logger)
{
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string Verifier, string RedirectUri, DateTimeOffset Expires)> _pending = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AccountTokens? _tokens = store.Load();

    public AccountStatus Status => new(options.SignInConfigured, _tokens is not null, _tokens?.Email, _tokens?.Name);

    public bool SignedIn => _tokens is not null;

    /// <summary>Starts a sign-in: remembers state and verifier, returns the provider URL to open in the browser.</summary>
    public async Task<string> BeginSignInAsync(string redirectUri, CancellationToken cancellationToken)
    {
        if (!options.SignInConfigured)
        {
            throw new InvalidOperationException("Sign-in is not set up in this build.");
        }
        foreach (var stale in _pending.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToList())
        {
            _pending.TryRemove(stale, out _);
        }

        var state = Pkce.RandomUrlSafe();
        var verifier = Pkce.RandomUrlSafe(48);
        _pending[state] = (verifier, redirectUri, DateTimeOffset.UtcNow + PendingLifetime);
        return await oidc.AuthorizeUrlAsync(state, Pkce.Challenge(verifier), redirectUri, cancellationToken);
    }

    /// <summary>Completes a sign-in from the provider's redirect. The state must match one this app started.</summary>
    public async Task CompleteSignInAsync(string state, string code, CancellationToken cancellationToken)
    {
        if (!_pending.TryRemove(state, out var pending) || pending.Expires < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("This sign-in link is not valid or has expired. Start again from the app.");
        }

        var tokens = await oidc.ExchangeCodeAsync(code, pending.Verifier, pending.RedirectUri, cancellationToken);
        var info = await oidc.UserInfoAsync(tokens.AccessToken, cancellationToken);
        _tokens = new AccountTokens(
            tokens.AccessToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600),
            info?.Subject,
            info?.Email,
            info?.Name);
        store.Save(_tokens);
        logger.LogInformation("Signed in as {Email}.", info?.Email ?? "unknown");
    }

    /// <summary>A valid access token, refreshed if it expires within a minute; null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var current = _tokens;
        if (current is null)
        {
            return null;
        }
        if (current.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return current.AccessToken;
        }
        if (current.RefreshToken is null)
        {
            return current.AccessToken; // let the server decide; it answers 401 if the token is no longer valid
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is { } latest && latest.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return latest.AccessToken;
            }
            var refreshed = await oidc.RefreshAsync(current.RefreshToken, cancellationToken);
            _tokens = current with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? current.RefreshToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn ?? 3600),
            };
            store.Save(_tokens);
            return _tokens.AccessToken;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            logger.LogWarning(ex, "Could not refresh the sign-in; signing out.");
            SignOutLocally();
            return null;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var refresh = _tokens?.RefreshToken;
        SignOutLocally();
        if (refresh is not null && options.SignInConfigured)
        {
            try
            {
                await oidc.RevokeAsync(refresh, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogInformation("Could not revoke the token at the provider; it was removed from this PC.");
            }
        }
    }

    private void SignOutLocally()
    {
        _tokens = null;
        store.Delete();
    }
}
