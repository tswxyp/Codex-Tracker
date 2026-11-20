# Codex Tracker

[![Latest stable release](https://img.shields.io/github/v/release/tswxyp/Codex-Tracker?label=release&logo=github)](https://github.com/tswxyp/Codex-Tracker/releases/latest)
[![Windows installer downloads across releases](https://img.shields.io/github/downloads/tswxyp/Codex-Tracker/CodexTracker-Setup-x64.exe?displayAssetName=false&label=installer%20downloads)](https://github.com/tswxyp/Codex-Tracker/releases)
[![Windows 11 x64](https://img.shields.io/badge/Windows%2011-x64-0078D4)](#quick-start)
[![Project license](https://img.shields.io/github/license/tswxyp/Codex-Tracker?label=license)](LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/tswxyp/Codex-Tracker?style=flat&label=Stars)](https://github.com/tswxyp/Codex-Tracker/stargazers)

[简体中文](README.md) · **English** · [日本語](README.ja.md) · [한국어](README.ko.md)

**What did you earn on that client project after paying for Codex?** Codex Tracker is a Windows tray app for OpenAI Codex Desktop that tracks token usage per chat, estimates subscription costs, and helps you work out profit or loss on client projects. Hover over a chat in the Codex sidebar to see its usage and cost. Enter income for a chat or a project to see its profit or loss. I believe most of you come out ahead on most jobs. Click **Recent profit and loss** on the card to see how that chat did over the period you choose.

**[Download for Windows x64](https://github.com/tswxyp/Codex-Tracker/releases/latest/download/CodexTracker-Setup-x64.exe)** · [Get started](#quick-start) · [How costs are estimated](#cost) · [Self-hosting](#self-hosting)

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/readme/en-dark.png">
  <img src="docs/images/readme/en-light.png" alt="A delivery chat cost an estimated CNY 14. The seven-day report shows CNY 800 received, CNY 20 in total costs and CNY 780 left." width="1146">
</picture>

*The actual interface with a fictional client job, shown in Chinese yuan (CNY). Left is the delivery chat. Right is the week's income and costs across all local chats.*

The client paid **CNY 800** in two instalments. The delivery chat used an estimated **CNY 14** of the subscription, leaving **CNY 786**. An unpaid revision in a separate chat cost another **CNY 6**. That leaves **about CNY 780** on the job after Codex costs. Labour, taxes and other expenses are separate.

<a id="quick-start"></a>

## Get started on Windows

Run the installer on a Windows 11 x64 PC with Codex Desktop. It includes the .NET desktop runtime and starts Tracker in the notification area. If you use 1.3.3 or a source build, exit the old tray app before installing once. Your existing ledger and settings carry over. [Download for Windows x64](https://github.com/tswxyp/Codex-Tracker/releases/latest/download/CodexTracker-Setup-x64.exe)

Tracker downloads future stable releases from GitHub. When the tray shows “Update ready”, restart Tracker from the menu or let it apply the update on its next start. [Release and update guide (Chinese)](docs/desktop-release.md)

<details>
<summary>Build from source (developers)</summary>

Install the [Windows x64 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and run the following in PowerShell.

```powershell
git clone https://github.com/tswxyp/Codex-Tracker.git
cd Codex-Tracker
.\scripts\build-phase2.ps1
& .\artifacts\phase2\windows-x64\CodexTracker.Tray.exe
```

</details>

1. Right-click the Tracker icon in the Windows notification area. Select **Enter subscription fee**, choose the currency you were billed in and enter what you actually pay each month. You can also enable launch at Windows sign-in.
2. Hover over a chat title in the Codex sidebar. On the first run, figures appear as Tracker reads your history in the background. The card follows the light or dark appearance of the sidebar.
3. Select **Enter chat income** or **Enter project income** on the card. Add the amount and payment date for each instalment. A project uses its own recorded income if entered. Otherwise, it adds up receipts from its chats. Each payment is counted once.

## What you can see

| Question | Where to look |
| --- | --- |
| What used the tokens? | The hover card shows total, input and output tokens, cache hit rate and models used. Reasoning tokens are already included in output. |
| How much did unpaid work cost? | The card shows both chat and project profit, with a separate figure for the cost of chats with no revenue. |
| What did I earn this month? | Open **Recent profit and loss** on a card for that chat, or double-click the tray icon for all local chats. See a trend and daily breakdown for 7, 30 or 90 days, the current month or a custom range. |
| Can I use my own language and currency? | The app supports English, Simplified Chinese, Japanese and Korean, and 11 display currencies. Use Windows defaults or choose your own. Reports have their own currency selector. |

Reports count income on the **payment date** and costs on the **usage date**. Receipts without a date appear in lifetime totals. Add the payment date to include them in a date-range report.

<a id="cost"></a>

## How a chat gets its estimated cost

1. **Work out a weekly budget.** Multiply the subscription payment by 7 and divide by the number of days in the billing period. With the default 30-day period, CNY 1,200 a month gives a weekly budget of CNY 280.
2. **Estimate the chat’s share of the weekly allowance.** Tracker weights tokens by model, uncached input, cached input, output and speed tier. Dividing the chat’s weighted usage by **335.669685** gives its estimated share in percentage points.
3. **Multiply the weekly budget by that share.** If the delivery chat’s weighted tokens represent an estimated 5% of the weekly allowance, its cost is **CNY 280 × 5% ≈ CNY 14**. This is an allocation of your subscription payment.

**Where does the estimate apply?** The fixed rate comes from past observations on a $200 Pro subscription. Its accuracy on other plans is unverified. On the same app version, activity on another device and sample uploads have no effect on the rate. Tracker flags a standard-speed assumption if the speed is missing. The card identifies usage it cannot price, and reports show **N/A** for an unavailable amount.

## How accurate is the cost estimate?

All Codex usage costs shown here are estimates. You can contribute usage samples to our Cloudflare-hosted service while one task runs in a single chat on one device. Maintainers can review accepted samples and use them to decide whether to change the fixed rate in a future release. Individual chat costs remain estimates.

A background worker reads new entries in active and archived Codex logs. It checks per-response usage against cumulative snapshots, removes duplicates and saves the ledger and its reading position in local SQLite. After a restart it picks up where it left off. Windows UI Automation locates chat rows for the hover card. The integration leaves the Codex installation, configuration and original chats intact. Ambiguous matches, including duplicate titles, are flagged.

- **Local use.** Usage tracking and income entry work without a Tracker account. Currency conversion uses daily reference rates from Frankfurter. When choosing a currency automatically for the English interface, the app queries IPinfo for the country associated with your public IP.
- **Optional calibration samples.** Sharing is off by default and requires fresh consent after sign-in. While a single task runs on one device, the client can send Cloudflare model and token counts, allowance changes and local task-overlap statistics. The payload excludes chat IDs, titles, conversation text, code, credentials and financial amounts. Cloudflare can see the connection IP. The sample database does not store it. Turning sharing off, pausing monitoring, locking Windows or exiting stops uploads and clears pending local samples. [Client sampling code](client/Tracker.Tray/CalibrationSample.cs) · [Server-side checks](deploy/calibration-host/worker.js)

<a id="self-hosting"></a>

## Optional self-hosted services

To combine usage from a desktop and a laptop, set up the sync service. Authorize both devices under one Tracker account and choose which chats to sync. Each device records usage offline and syncs on reconnect, with duplicate events counted once. [Setup and device authorization](docs/phase-3-run.md)

The optional ASP.NET Core service uses HTTPS and PostgreSQL and is currently a release candidate. The desktop client has been tried on Windows 11 x64. Windows 10 hardware verification, a two-computer sync trial and a live Docker Compose deployment are still pending. The technical guides below are in Chinese.

[Desktop settings, sampling and CLI](docs/desktop-guide.md) · [Architecture](ARCHITECTURE.md) · [Database](docs/database.md) · [Usage accuracy](docs/phase-1-acceptance.md) · [Desktop compatibility](docs/phase-2-compatibility.md) · [Sync verification](docs/phase-3-implementation.md) · [Changelog](CHANGELOG.md) · [MIT license](LICENSE) · [Third-party notices](docs/third-party.md)
