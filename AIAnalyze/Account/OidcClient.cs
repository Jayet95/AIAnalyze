using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AIAnalyze.Account;

/// <summary>Settings for accounts and the AIAnalyze server, bound from the "Cloud" config section.</summary>
public sealed class CloudOptions
{
    /// <summary>OpenID Connect issuer of the sign-in provider, for example https://your-app.clerk.accounts.dev.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>OAuth client ID of this desktop app (a public client: PKCE, no secret).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Base URL of the AIAnalyze API (the Cloudflare Worker), for example https://api.example.com.</summary>
    public string ApiBaseUrl { get; set; } = "";

    public string Scopes { get; set; } = "openid profile email offline_access";

    public bool SignInConfigured => Issuer.Length > 0 && ClientId.Length > 0;
    public bool ApiConfigured => ApiBaseUrl.Length > 0;
}

/// <summary>Tokens returned by the provider's token endpoint.</summary>
public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("id_token")] string? IdToken);

public sealed record DiscoveryDocument(
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string? UserinfoEndpoint,
    [property: JsonPropertyName("revocation_endpoint")] string? RevocationEndpoint);

public sealed record UserInfo(
    [property: JsonPropertyName("sub")] string? Subject,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>PKCE values (RFC 7636): a random verifier, its S256 challenge, and a random state.</summary>
public static class Pkce
{
    public static string RandomUrlSafe(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Minimal OpenID Connect client for a desktop app: authorization code flow with PKCE and a loopback
/// redirect (RFC 8252), token refresh, userinfo and revocation. Works with any standard provider (Clerk,
/// Auth0, Entra, Supabase); only the issuer and client ID change.
/// </summary>
public sealed class OidcClient(HttpClient http, CloudOptions options)
{
    private DiscoveryDocument? _discovery;

    public async Task<DiscoveryDocument> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_discovery is not null)
        {
            return _discovery;
        }
        var url = $"{options.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        _discovery = await http.GetFromJsonAsync<DiscoveryDocument>(url, cancellationToken)
            ?? throw new InvalidOperationException("The sign-in provider returned no configuration.");
        return _discovery;
    }

    public async Task<string> AuthorizeUrlAsync(string state, string challenge, string redirectUri, CancellationToken cancellationToken)
    {
        var doc = await DiscoverAsync(cancellationToken);
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = options.Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        var separator = doc.AuthorizationEndpoint.Contains('?') ? "&" : "?";
        return doc.AuthorizationEndpoint + separator + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<TokenResponse> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken) =>
        await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = options.ClientId,
            ["code_verifier"] = verifier,
        }, cancellationToken);

    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) =>
        await PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId,
        }, cancellationToken);

    public async Task<UserInfo?> UserInfoAsync(string accessToken, CancellationToken cancellationToken)
    {
        var doc = await DiscoverAsync(cancellationToken);
        if (doc.UserinfoEndpoint is null)
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, doc.UserinfoEndpoint);
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<UserInfo>(cancellationToken) : null;
    }

    /// <summary>Asks the provider to revoke a refresh token. Best effort: sign-out works locally either way.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var doc = await DiscoverAsync(cancellationToken);
        if (doc.RevocationEndpoint is null)
        {
            return;
        }
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
            ["client_id"] = options.ClientId,
        });
        using var _ = await http.PostAsync(doc.RevocationEndpoint, content, cancellationToken);
    }

    private async Task<TokenResponse> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var doc = await DiscoverAsync(cancellationToken);
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(doc.TokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The sign-in provider rejected the request ({(int)response.StatusCode}).");
        }
        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("The sign-in provider returned no tokens.");
    }
}
