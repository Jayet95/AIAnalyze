using System.Net;
using System.Net.Http.Json;
using AIAnalyze.Account;
using AIAnalyze.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace AIAnalyze.Web;

/// <summary>
/// Browser sign-in for the desktop app. /auth/sign-in sends the browser to the provider; the provider sends it
/// back to /auth/callback on this PC's loopback address with a one-time code.
/// </summary>
public static class AccountEndpoints
{
    public static void MapAccount(this WebApplication app)
    {
        app.MapGet("/auth/sign-in", async (HttpRequest request, AccountService account, CancellationToken ct) =>
        {
            try
            {
                return Results.Redirect(await account.BeginSignInAsync(RedirectUri(request), ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return Page("Sign-in isn't available", ex is HttpRequestException
                    ? "The sign-in service could not be reached. Check the internet connection and try again."
                    : ex.Message);
            }
        });

        app.MapGet("/auth/callback", async (string? code, string? state, string? error, string? error_description, AccountService account, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                return Page("Sign-in was cancelled", error_description ?? error);
            }
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return Page("Sign-in failed", "The sign-in response was incomplete. Start again from the app.");
            }
            try
            {
                await account.CompleteSignInAsync(state, code, ct);
                return Results.Redirect("/#signed-in");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                return Page("Sign-in failed", ex.Message);
            }
        });

        app.MapGet("/api/account", async (AccountService account, ChatRouter chat, CloudOptions cloud, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var status = account.Status;
            object? quota = null;
            if (status.SignedIn && cloud.ApiConfigured)
            {
                quota = await TryQuota(factory.CreateClient("cloud"), cloud, account, ct);
            }
            return Results.Ok(new
            {
                status.SignInConfigured,
                status.SignedIn,
                status.Email,
                status.Name,
                assistant = chat.Mode.ToString(),
                quota,
            });
        });

        app.MapPost("/api/account/sign-out", async (HttpRequest request, AccountService account, CancellationToken ct) =>
        {
            if (!ChatEndpoints.IsAllowedOrigin(request))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            await account.SignOutAsync(ct);
            return Results.Ok(new { signedIn = false });
        });
    }

    /// <summary>The loopback redirect registered with the provider: http://127.0.0.1:{port}/auth/callback.</summary>
    public static string RedirectUri(HttpRequest request) => $"http://127.0.0.1:{request.Host.Port ?? 5280}/auth/callback";

    private static async Task<object?> TryQuota(HttpClient http, CloudOptions cloud, AccountService account, CancellationToken ct)
    {
        try
        {
            var token = await account.GetAccessTokenAsync(ct);
            if (token is null)
            {
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{cloud.ApiBaseUrl.TrimEnd('/')}/v1/me");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<QuotaResponse>(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record QuotaResponse(int QuestionsToday, int DailyLimit);

    private static IResult Page(string title, string message) =>
        Results.Content($$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>body{font:15px system-ui;max-width:520px;margin:15vh auto;padding:0 16px;color:#161a23}a{color:#3b6cf6}</style></head>
            <body><h2>{{WebUtility.HtmlEncode(title)}}</h2><p>{{WebUtility.HtmlEncode(message)}}</p><p><a href="/">Back to AIAnalyze</a></p></body></html>
            """, "text/html");
}
