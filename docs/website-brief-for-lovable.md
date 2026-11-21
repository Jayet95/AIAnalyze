# AIAnalyze website: plan and Lovable brief

The website is where people learn what AIAnalyze is, download the installer, and create the account the assistant uses. It is a separate Lovable project from the app UI.

---

## 1. Goals, in order

1. **Make the value clear in five seconds:** "it finds what's wrong with your PC and tells you how to fix it, in plain words."
2. **Get the download:** one obvious button, everywhere.
3. **Earn trust:** what it reads, what stays on the PC, what never happens, a signed installer, honest limits.
4. **Create accounts:** sign-up unlocks the assistant (free during the beta). The app works without an account.
5. **Help people succeed after installing:** what to do first, what PawnIO and PresentMon are, how to uninstall.

## 2. Audience

- **Primary:** gamers and everyday Windows users whose PC is slow, hot, noisy, crashing or stuttering, and who don't know why.
- **Secondary:** enthusiasts who want one place for health, temperatures, fans, RAM and FPS, with an assistant that reads the actual numbers.

Write for the primary audience. Enthusiasts find the details on How it works.

## 3. Messaging

**Tagline options** (pick one):
- "Your PC's doctor. It notices problems and tells you what to do."
- "Find out what's wrong with your PC, in plain words."
- "Know why your PC is slow, hot or crashing."

**Three pillars**
1. **Notices problems for you.** Every 10 minutes it checks for crashes, hardware errors, driver resets, failing drives, low disk space, memory pressure, overheating and more.
2. **Explains them in plain words.** Every problem says what it means and what to do. Ask the assistant anything; it answers with your PC's real numbers.
3. **Private by default.** Everything is measured and stored on your PC. Nothing is shared unless you choose to.

**Honesty rules for all copy:** no "AI fixes everything", no "boost FPS by X%", no fake numbers of users or reviews. AIAnalyze reads and explains; it never changes settings.

## 4. Pages

| Path | Purpose | Sign-in |
|---|---|---|
| `/` | Home: value, proof, download | no |
| `/download` | Installer, version, checksum, requirements, first steps | no |
| `/how-it-works` | What it checks and how, for curious users | no |
| `/privacy` | Plain-language summary plus the full policy | no |
| `/terms` | Terms of use | no |
| `/faq` | Common questions | no |
| `/sign-in`, `/sign-up` | Clerk sign-in and sign-up | no |
| `/account` | Plan, assistant usage, connect the app, manage account | yes |

### 4.1 Home (`/`)

1. **Hero:** tagline, one-line subhead ("A free Windows app that checks your PC's health, explains problems in plain words, and helps you fix them."), primary button **Download for Windows**, small line under it "Free · Windows 10 and 11 · 64-bit", secondary link "See how it works". Next to it, a large screenshot of the Health screen.
2. **Three pillars** (section 3).
3. **What it checks:** a grid of 12 small cards with an icon and one line each: Crashes and blue screens · Hardware errors · Graphics driver · Program crashes · Background services · Windows Update · Disk space · Drive health · Memory and RAM setup · Programs using the CPU · Temperatures and fans · Battery.
4. **The assistant:** a mock chat. The user asks "Why is my PC slow?" and the answer quotes numbers: "Memory is 94% full: Chrome uses 6.1 GB… Close some tabs, or add RAM." Caption: "Answers use your PC's real measurements, not guesses."
5. **For gamers:** an FPS chart with a dip and the finding "Your GPU reached 87 °C and slowed itself down; clean the fans."
6. **How it works in three steps:** Install (one setup file, runs quietly in the background) → It watches (health every 10 minutes, sensors every second) → You get answers (alerts when something changes, ask anything anytime).
7. **Privacy strip:** three short facts with icons: Stored on your PC · Sharing is off unless you turn it on · Reads only, never changes your settings. Link to `/privacy`.
8. **FAQ teaser:** four questions, link to `/faq`.
9. **Final call to action:** "Find out what your PC is trying to tell you." with **Download for Windows**.

### 4.2 Download (`/download`)

- Big **Download for Windows** button linking to `https://github.com/Jayet95/AIAnalyze/releases/latest/download/AIAnalyze-Setup.exe`.
- Version and date, file size, SHA-256 from `SHA256SUMS.txt`, and "Signed by …" once the installer is signed.
- Requirements: Windows 10 or 11, 64-bit, about 150 MB of disk space, administrator rights to install.
- **After installing:** AIAnalyze opens in your browser at `127.0.0.1:5280`; it's in the Start menu as AIAnalyze. Optional: install the free, signed PawnIO driver for CPU temperature and fan readings. FPS in games works out of the box (PresentMon is included).
- **If Windows shows a SmartScreen warning:** explain honestly that new apps build reputation over time, how to check the signature, and "More info → Run anyway". Remove this note once the signed installer has reputation.
- **Uninstall:** Settings → Apps → AIAnalyze; it asks whether to delete the collected data.
- Link to the release notes (GitHub releases).

### 4.3 How it works (`/how-it-works`)

- **A background service** samples sensors every second and checks health every 10 minutes, using about 0.6% of one CPU core and 155 MB of memory.
- **The local app** opens in your browser but only works on your PC (it listens on 127.0.0.1).
- **Health checks:** one paragraph each for the 13 checks, with an example of what it says.
- **Temperatures, fans and RAM:** what is measured, the inferred cooling type, the RAM slot map, and the single-channel check.
- **Games:** FPS per game against your usual FPS, and findings like GPU-bound, frame cap, throttling and stutter.
- **The assistant:** what it receives (a summary of your PC's measurements), what it can't do (change anything), the daily free limit.
- **What it needs:** administrator rights to install, PawnIO for some readings.

### 4.4 Privacy (`/privacy`)

Start with the plain-language summary in `installer/privacy.txt` (same facts, same words), then the full policy. The full policy and the terms need review by a lawyer before launch; until then mark them as drafts. Include: who runs the service and how to reach them; what is collected locally, what is sent and when (assistant questions, opt-in diagnostics, account email); where data is stored (Cloudflare, Clerk, Anthropic for assistant questions); retention (diagnostics 90 days, usage counters 60 days); how to delete your account and data; contact for privacy requests.

### 4.5 FAQ (`/faq`)

- **Is it free?** Yes during the beta, including the assistant up to a daily limit.
- **Does it change my settings or "optimize" my PC?** No. It reads and explains. You decide what to do.
- **Is my data sent anywhere?** Only what you choose: questions you ask the assistant, and anonymous diagnostics if you turn sharing on.
- **Do I need an account?** No. The account is needed only for the assistant.
- **Why does it need administrator rights?** To run as a service and read hardware sensors.
- **Will it slow down my PC or games?** It uses about 0.6% of one CPU core.
- **Why are some readings blank?** Laptops and some motherboards don't report fans; CPU temperature needs the PawnIO driver.
- **Is it safe? My antivirus warned me.** The installer is signed (once it is), the app only reads data, and it uses the signed PawnIO driver, not the old WinRing0 that antivirus flags. Report false positives to us.
- **Which Windows versions?** Windows 10 and 11, 64-bit.
- **How do I uninstall?** Settings → Apps → AIAnalyze.

### 4.6 Account (`/account`)

- Header: name, email, avatar from Clerk.
- **Plan:** "Free beta". **Assistant questions today:** `questionsToday` of `dailyLimit` with a bar, from `GET {API}/v1/me` with `Authorization: Bearer <Clerk session token>` (`await getToken()`). Response: `{ "userId": "...", "questionsToday": 4, "dailyLimit": 30 }`. On 401, show "Sign in again".
- **Connect the app:** "Open AIAnalyze on your PC and click Sign in. It opens this site; sign in, and the app connects." With a small illustration.
- **Manage account:** Clerk's `<UserProfile />` (email, password, connected Google account, sessions).
- **Delete account:** a button that explains what is deleted and links to a privacy request (email) until self-service deletion exists.

## 5. Sign-in with Clerk

- Use the **same Clerk application** as the desktop app, so one account works in both.
- Website: `@clerk/clerk-react` with the **publishable key** (public, safe in browser code). `<SignIn />` and `<SignUp />` on their pages, `<SignedIn>` / `<SignedOut>` in the header, `<UserButton />` when signed in, `/account` protected.
- Sign-in methods: email (with verification) and Google.
- Never put the Clerk **secret** key in the website.
- The desktop app signs in through Clerk's OAuth application with the redirect `http://127.0.0.1:5280/auth/callback` (see `server/README.md`). The website does not need to do anything for that flow.

## 6. Hosting and setup

- **Website:** Cloudflare Pages (static build). Add `public/_redirects` with `/* /index.html 200` for client routing.
- **Domain:** buy it on Cloudflare (for example `aianalyze.app`); point the root to Pages and `api.` to the Worker.
- **Worker:** set `SITE_ORIGIN` in `server/worker/wrangler.toml` to the site's address (for example `https://aianalyze.app`), so the account page can read the quota. Nothing else changes.
- **Downloads:** GitHub Releases in the AIAnalyze repository (it must be public), with `AIAnalyze-Setup.exe`, the versioned exe and `SHA256SUMS.txt`, all produced by `scripts\build-installer.ps1`. Later, Cloudflare R2 if the repository becomes private.
- **Analytics:** none, or Cloudflare Web Analytics (no cookies, no personal data). No third-party trackers or ad pixels; it would contradict the privacy story. Clerk's own cookies are essential, so no cookie banner is needed without trackers (confirm with the lawyer).

## 7. Design direction

Same family as the app: calm, trustworthy, a "check-up for your PC" rather than an RGB gamer site. Generous white space, one accent colour, real app screenshots (made from the demo data, not someone's real PC), dark and light themes, Inter font bundled. Severity colours only where they mean something. Every page has the Download button in the header.

## 8. SEO basics

Page titles and descriptions per page, an Open Graph image (the Health screenshot with the tagline), `sitemap.xml`, `robots.txt`, real headings, alt text on screenshots. Target searches: "why is my pc slow", "pc overheating check", "blue screen cause", "gpu throttling", "fps drop cause".

## 9. Prompt to paste into Lovable

> Build the marketing and account website for **AIAnalyze**, a free Windows app that checks a PC's health, explains problems in plain words and helps fix them. React + TypeScript + Tailwind, deployed as a static site on Cloudflare Pages (add `public/_redirects` with `/* /index.html 200`).
>
> Pages: `/` (home), `/download`, `/how-it-works`, `/privacy`, `/terms`, `/faq`, `/sign-in`, `/sign-up`, `/account` (signed in only). Use the structure and copy in the attached brief (sections 3 and 4) as written.
>
> Header on every page: logo, How it works, Download, FAQ, Privacy, and Clerk `<SignedOut>` "Sign in" / `<SignedIn>` `<UserButton />`, plus a primary **Download for Windows** button linking to `https://github.com/Jayet95/AIAnalyze/releases/latest/download/AIAnalyze-Setup.exe`.
>
> Sign-in: `@clerk/clerk-react` with the publishable key from an environment variable (`VITE_CLERK_PUBLISHABLE_KEY`); email and Google; `/account` protected. The account page calls `GET ${VITE_API_BASE_URL}/v1/me` with `Authorization: Bearer ${await getToken()}` and shows "Assistant questions today: N of M" from `{ questionsToday, dailyLimit }`; on 401 show "Sign in again"; when the API URL is not set, show the plan without usage.
>
> Feel: calm and trustworthy, a check-up for your PC, not an RGB gamer site. Real app screenshots, generous spacing, one accent colour, dark and light themes, Inter bundled. No third-party trackers, no remote fonts, no fake testimonials or user counts. Never claim the app changes settings or boosts FPS: it reads and explains.

Attach this file and screenshots of the app (Health, Sessions, Live, chat).

## 10. Launch checklist

- [ ] Domain bought and on Cloudflare.
- [ ] Clerk production instance on the domain; Google sign-in configured; OAuth application for the desktop app with redirect `http://127.0.0.1:5280/auth/callback`.
- [ ] Worker deployed with `OIDC_ISSUER`, `OIDC_CLIENT_ID`, `SITE_ORIGIN`, the D1 database and the `ANTHROPIC_API_KEY` secret; a spending limit on the Anthropic account.
- [ ] App built with the `Cloud` settings (issuer, client ID, API URL).
- [ ] Installer built, **signed**, and uploaded to a GitHub release with `SHA256SUMS.txt`.
- [ ] Privacy policy and terms reviewed by a lawyer.
- [ ] End-to-end test on a clean PC: download from the site → install → sign in from the app → ask the assistant → see usage on `/account`.
