> **DRAFT for legal review.** Not yet in force. Fill in the bracketed items and have a lawyer review this policy (Israeli Privacy Protection Law, GDPR for users in the EU/UK, and any other market you launch in) before real users sign up.

# AIAnalyze Privacy Policy

**Effective date:** [date]
**Operator:** [legal name of the person or company] ("we", "us"), [address]. Contact: [privacy email].

AIAnalyze is a Windows app that checks your PC's health and explains problems. This policy explains what information it uses, where it goes, and the choices you have.

## 1. Summary

- AIAnalyze measures your PC **on your PC** and stores the results **on your PC**.
- Nothing leaves your PC unless you **sign in**, **ask the assistant a question**, **turn on email alerts**, or **turn on anonymous diagnostics**.
- We don't sell personal information, we don't show ads, and we don't use trackers on the app or the website.
- AIAnalyze only reads information. It doesn't change your settings, install drivers, or delete anything.

## 2. Information that stays on your PC

The app collects the following and stores it in `C:\ProgramData\AIAnalyze` on your PC. We don't receive it.

| Information | Examples | Kept for |
|---|---|---|
| Hardware and Windows details | PC model, processor, memory sticks (slot, size, speed, maker, part number), graphics cards and driver versions, motherboard and BIOS version, Windows version | Until it changes |
| Sensor readings | CPU and GPU temperature, load, power, clock, fan speeds | 2 days |
| Health checks | Crashes and blue screens, hardware errors, driver resets, programs that crash or use the most CPU or memory (by program name), failing services, failed updates, disk space and drive health, battery wear | Latest result, 1 day of history |
| Game sessions | Game file name, frames per second, findings | Sessions kept for your personal baseline; per-second data 30 days |
| Alerts, settings, your Helpful / Not helpful votes | | 30 days (alerts), until changed (settings) |

Serial numbers, your Windows user name and your computer's name are not collected. The uninstaller offers to delete all of this.

## 3. Information that leaves your PC, and why

### 3.1 Your account (if you sign up)

To create and use an account we process your **email address**, **name** (if you provide one), and sign-in identifiers. Sign-in is handled by our authentication provider (Clerk). If you sign in with Google, Google shares your name and email with us. The app receives a sign-in token, never your password.

### 3.2 Questions to the assistant

When you ask the assistant a question, or when it reviews a game session for you, we send **your question** and a **briefing about your PC** to our server, which passes them to our AI provider (Anthropic) to produce the answer. The briefing contains the information in section 2 that is relevant to answering: hardware and drivers, health check results, current readings, recent alerts and sessions. To answer, the assistant can also ask the app for more of this information (for example, Windows event entries from the last 30 days, temperature and load history from the last 2 days, or one game session in detail); the app looks it up on your PC and sends only the result. The assistant can only read; it cannot change anything on your PC. The briefing and these results **can include names of programs** (for example, a program that crashed or uses the most memory) and **short Windows event messages**.

- Our server doesn't store the content of questions, briefings or answers. It stores a count of questions per account per day, to apply the free daily limit (kept 60 days).
- Anthropic processes the content under its commercial terms for API customers; see [link to Anthropic's commercial terms and privacy information].
- If you use your **own Anthropic API key** instead of an account, questions go directly from your PC to Anthropic under your agreement with them, and don't pass through our server.

### 3.3 Anonymous diagnostics (off unless you turn it on)

If you turn on sharing in Settings, the app sends, about once a day:

- hardware models, driver versions and Windows build;
- which health checks failed and which types of findings appeared (not their text);
- game file names, number of sessions, minutes played and median FPS;
- your Helpful / Not helpful votes on checks, findings and answers;
- a random identifier created when the app was installed, and your account identifier if you are signed in.

It **never** includes names of your files, folders or other programs, Event Log text, or chat content. Settings shows exactly what would be sent. You can turn it off at any time; we keep diagnostics for 90 days.

### 3.4 Email alerts (off unless you turn them on)

If you are signed in and turn on "Email me about serious problems" in Settings, the app sends our server the **title and description of a serious problem** it found (for example "Drive health: Windows reports ST2000DM008 as unhealthy"), and our server emails it to **your account's email address** through our email provider (Resend). The description can include hardware and program names. At most 5 emails a day. We store only a count of emails per account per day (kept 60 days), not their content. Turn it off in Settings at any time.

### 3.5 The website

When you visit [website address], our hosting provider (Cloudflare) processes your IP address and request details to deliver the site and protect it from abuse. Our authentication provider (Clerk) uses cookies that are needed to keep you signed in. [If enabled: we use Cloudflare Web Analytics, which doesn't use cookies or identify you.] Installer downloads are served by GitHub.

## 4. Why we use it (legal bases, for users in the EU/UK)

- **To provide the service you asked for** (contract): your account, the assistant, the daily limit.
- **With your consent**: anonymous diagnostics. You can withdraw consent in Settings at any time.
- **Legitimate interests**: keeping the service secure and preventing abuse (for example, rate limits and security logs).

## 5. Who we share it with

We use these service providers, only to run AIAnalyze:

| Provider | What for |
|---|---|
| Clerk | Accounts and sign-in |
| Cloudflare | Hosting the website and our server, storing usage counters and diagnostics |
| Anthropic | Generating the assistant's answers |
| Resend | Sending email alerts, if you turn them on |
| GitHub | Hosting installer downloads |

We don't sell or rent personal information, and we don't share it with advertisers. We may disclose information if the law requires it.

## 6. International transfers

Our providers may process information outside your country, including in the United States. Where required, transfers rely on appropriate safeguards such as the European Commission's Standard Contractual Clauses. [Confirm per provider.]

## 7. How long we keep it

| Information | Kept for |
|---|---|
| Account | Until you delete your account |
| Questions per day (count only) | 60 days |
| Email alerts per day (count only) | 60 days |
| Anonymous diagnostics and votes | 90 days |
| Question and answer content | Not stored by us; see Anthropic's terms |
| Website security logs | As kept by Cloudflare [confirm] |

## 8. Your rights

Depending on where you live, you can ask to access, correct or delete your information, object to or restrict its use, receive a copy, and withdraw consent. Contact [privacy email]. You can also complain to your data protection authority (in Israel, the Privacy Protection Authority; in the EU/UK, your local authority).

To delete your account and the data tied to it, [use the Delete account option on the account page / email us]. Data on your PC is deleted by uninstalling AIAnalyze and choosing to delete its data, or by deleting `C:\ProgramData\AIAnalyze`.

## 9. Security

The app's local page is reachable only from your PC. Sign-in tokens and API keys are stored in files only Windows administrators can read. Connections to our server use HTTPS. No system is perfectly secure; tell us at [security email] if you find a problem.

## 10. Children

AIAnalyze is not directed at children under [13 / 16, per jurisdiction], and we don't knowingly collect their information.

## 11. Changes

We'll post changes on this page and update the effective date. For significant changes we'll notify you in the app or by email.

## 12. Contact

[legal name], [address], [privacy email].
