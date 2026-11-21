# AIAnalyze server

A Cloudflare Worker that gives signed-in users the assistant without their own API key, and receives opt-in diagnostics.

- `POST /v1/chat`: checks the user's sign-in token, counts the question against a daily free quota, and relays it to Claude with **your** Anthropic API key. The Worker owns the assistant's instructions and tool definitions (it imports `AIAnalyze/Chat/Assistant/instructions.txt` and `tools.json`, the same files the app embeds, so deploy from a full checkout of the repository). When the model asks for a tool, the Worker returns the tool call; the app runs it on the PC (read-only queries over its own data) and sends only the result in the next request. A question counts once; its tool rounds count against a separate, larger daily step quota (`steps` column).
- `GET /v1/me`: questions used today and the daily limit.
- `POST /v1/telemetry`: opt-in snapshots and Helpful / Not helpful votes (anonymous, or tied to the account when signed in).
- A daily job deletes telemetry and feedback after 90 days and usage counters after 60 days.

Sign-in is any OpenID Connect provider. These steps use **Clerk**.

## 1. Clerk

1. Create an application at clerk.com. Development is fine to start; production needs your own domain.
2. Turn on the sign-in methods you want (email, Google).
3. Under **OAuth applications**, create one for the desktop app:
   - Type: public client (PKCE, no client secret).
   - Redirect URI: `http://127.0.0.1:5280/auth/callback`
   - Scopes: `openid profile email offline_access`
4. Note two public values:
   - **Issuer**: the Frontend API URL, for example `https://your-app.clerk.accounts.dev`. It must serve `/.well-known/openid-configuration`.
   - **Client ID** of the OAuth application.

These two are public by design; they are not secrets.

## 2. Cloudflare

You need Node.js on the machine you deploy from.

```bash
cd server/worker
npm install
npm run typecheck
npx wrangler login
npx wrangler d1 create aianalyze
```

Copy the `database_id` it prints into `wrangler.toml`, and set `OIDC_ISSUER` (and optionally `OIDC_CLIENT_ID`) there. Then:

```bash
npx wrangler d1 execute aianalyze --remote --file=schema.sql
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler deploy
```

`wrangler secret put` asks for the key and stores it encrypted in Cloudflare. Paste your Anthropic key there and nowhere else: not in `wrangler.toml`, not in git, not in a chat.

`wrangler deploy` prints the Worker's URL, for example `https://aianalyze-api.<you>.workers.dev`. With your own domain, add a route such as `api.yourdomain.com`.

Set the daily free limit with `DAILY_FREE_QUESTIONS` in `wrangler.toml` (default 30). Set a monthly spending limit on your Anthropic account as a backstop.

### Optional: email alerts

Users who sign in can turn on "Email me about serious problems" in the app. The app sends `POST /v1/notify` with only the problem's title and description; the Worker emails it to the account's own address (from the sign-in token or the provider's userinfo, never from the request), at most 5 a day per user, and stores only that count (`email_usage`, 60 days).

1. Create an account at [Resend](https://resend.com) (free tier) and verify your domain there (it shows DNS records to add at Cloudflare).
2. Set the sender in `wrangler.toml`, for example `EMAIL_FROM = "AIAnalyze <alerts@yourdomain.com>"`.
3. Create a Resend API key with "sending access" only, and store it as a secret (paste it there and nowhere else):

   ```bash
   npx wrangler secret put RESEND_API_KEY
   npx wrangler d1 execute aianalyze --remote --file=schema.sql   # adds the email_usage table
   npx wrangler deploy
   ```

4. In the app: sign in, open Settings, turn on email alerts and click **Send a test email**.

Without `RESEND_API_KEY` and `EMAIL_FROM` the route answers "not set up" and the app sends nothing. In Clerk, make sure the OAuth application's scopes include `email`, so the email address is available.

## 3. The app

Put the three public values in the app's settings, either in `AIAnalyze/appsettings.json` before building (for every install) or in `C:\ProgramData\AIAnalyze\appsettings.json` on one PC:

```json
{
  "Cloud": {
    "Issuer": "https://your-app.clerk.accounts.dev",
    "ClientId": "your-oauth-client-id",
    "ApiBaseUrl": "https://aianalyze-api.<you>.workers.dev"
  }
}
```

Restart the service. **Sign in** appears in the top bar; the assistant then runs through the account, and the user's own API key becomes optional.

## Check it works

1. In the app, click **Sign in**, sign in in the browser, and you return to the app signed in.
2. Ask the assistant a question; the account menu shows "1 of 30" questions today.
3. Turn on sharing in Settings; within the hour, `npx wrangler d1 execute aianalyze --remote --command "SELECT COUNT(*) FROM telemetry"` shows a row.
