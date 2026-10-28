# RegWatch: code structure

A guide to the code for a developer picking RegWatch up. For what the app does from a user's point
of view, see [GUIDE.md](GUIDE.md); for setup and build commands, see the [README](../README.md).

RegWatch is a small, local Windows tool. It is not a product or a service, and nothing runs on a
server. Once a week it reads new IRS and Treasury material, has an AI model score each item 0–3
against a plain-text profile of what one practice cares about (US foreign trusts and international
tax), and writes a Markdown digest. Version 0.1.0-alpha, .NET 10, C#.

## Solution layout

```
RegWatch.slnx
Directory.Build.props        one <Version> for every project and the installer
src/
  RegWatch.Core/             net10.0 class library: fetching, scoring, digests, state, settings
  RegWatch.App/              net10.0-windows WPF desktop app (WebView2 shows the digests)
  RegWatch.Runner/           net10.0-windows console app for Task Scheduler (headless run)
tests/
  RegWatch.Tests/            xUnit, references Core only; Fixtures/ holds saved real pages
installer/                   Inno Setup script + build.ps1 (publish, then compile the setup .exe)
tools/make-icon.ps1          draws the app icon with WPF and writes the multi-size .ico
.github/workflows/           ci.yml (test on push/PR), release.yml (installer on a v* tag)
docs/                        GUIDE.md (built into the app as Help), this file, a publishing checklist
```

Everything that can be tested without a window is in **Core**. App and Runner are thin shells
around `RunPipeline`.

Packages: Markdig and Microsoft.Web.WebView2 (App); Anthropic SDK and
System.Security.Cryptography.ProtectedData (Core); xUnit (tests). No database, no web server.

> The repository root is also the original author's data folder (`PROFILE.md`, `STATE.json`,
> `digests/`, `PLAYBOOK.md` from the earlier agent-driven version). The app normally uses
> `%USERPROFILE%\RegWatch`; see "Where data lives" below.

## How one run works

`RunPipeline.RunAsync` (Core) does the whole run. The app's **Run now** and the Runner both call it.

```
PROFILE.md ─┐
STATE.json ─┼─► FetchAsync ──► FetchResult ──► IScorer.ScoreAsync ──► ScoringResult
settings ───┘     │                               (profile + JSON payload)      │
                  ├─ IrbClient            IRB issues since last_irb_issue         ▼
                  ├─ FederalRegisterClient  IRS / Treasury / FinCEN since date   DigestWriter
                  └─ FeedClient (per enabled source)                             digests/<date>.md + .json
                       ├─ RSS/Atom feeds                                          │
                       ├─ IrsNewsClient                                           ▼
                       ├─ WrittenDeterminationsClient  ┐ narrowed by           STATE.json bookmarks
                       ├─ TaxCourtClient               ├ ProfileTerms           advanced, saved
                       └─ TreasuryPressClient          ┘ (core sections/terms)
```

1. **Load.** Read `PROFILE.md`. If the setting is on, append the latest scoring corrections from
   `feedback.jsonl` (`FeedbackLog.ProfileAddendum`). Load `STATE.json` (`StateStore`).
2. **Fetch** (`FetchAsync`). Each source failure becomes a line in `Problems` instead of failing
   the run, and that source's bookmark is not advanced, so the next run retries it.
   - **IRB** (`IrbClient`): lists issues on irs.gov/irb and takes those after `last_irb_issue`
     (only the latest 4 on a first run). For each one it keeps the contents and the "Highlights of
     This Issue" synopses. It stops at the first failed issue so the bookmark can't skip past it.
   - **Federal Register** (`FederalRegisterClient`): the public documents.json API for three
     agencies, published on or after `last_fedreg_date` (30 days on a first run). Results are
     deduplicated against `seen_documents`.
   - **Other sources** (`FeedClient.GetItemsAsync`): a 14-day first-run window, then everything
     after each source's bookmark in `STATE.json` `feeds`. Items in `seen_feed_items` are dropped,
     and items with the same headline from two feeds are merged.
3. **Score.** One `IScorer.ScoreAsync(profile, fetched)` call. The scorer returns a
   `ScoringResult`: guidance `items` (cards) and `press` entries (everything from the other
   sources), each with a score, matched buckets and a one-line reason. If nothing new was fetched,
   no scorer is called.
4. **Write.** `DigestWriter` renders `digests/<date>.md` (a `_2`, `_3` … suffix when that day
   already has one) and saves the raw `ScoringResult` beside it as `.json`.
5. **Save state.** Bookmarks advance only past what was actually fetched. A Cancel or an exception
   anywhere before this point leaves `STATE.json` untouched, so the same window is fetched again.

`FetchAsync(log)` (public, no scorer) is the dry run: the Runner's `--dry-run` writes the exact
scoring payload to `logs\<date>-payload.json`.

## Core, file by file

**Pipeline and data**

| File | What it holds |
|---|---|
| `RunPipeline.cs` | The run (above); `DefaultRoot` (`--root`, then `REGWATCH_ROOT`, then `%USERPROFILE%\RegWatch`); `CreateHttpClient` (decompression, 60 s timeout, a `RegWatch/<version>` User-Agent, which irs.gov requires). |
| `Models.cs` | `IrbIssue`, `FedRegDocument`, `FetchResult`, and the scorer's output records `ScoredItem`, `PressItem` and `ScoringResult`. The JSON names are snake_case to match the scoring schema. |
| `StateStore.cs` | `STATE.json` as a raw `JsonObject`, so unknown fields survive. `RunState` exposes the bookmarks: `last_irb_issue`, `last_fedreg_date`, `feeds{url: newest date}`, and `seen_documents` / `seen_feed_items` (1,000 each). It saves through a temp file and a move. |
| `DigestWriter.cs` | Renders the Markdown: run notes, score-3/2/1 sections of `### … — relevance N` cards, "In the press", and a one-line-per-item Skip list. `Covers` links an article to the card it discusses by normalised cite. |
| `DigestIndex.cs` | Reads the digests folder back for the app's list (headline and score counts) and the comment-deadline table. It parses the Markdown, not the JSON, so older agent-written digests work too. |

**Sources**

| File | Source and method |
|---|---|
| `IrbClient.cs` | irs.gov/irb index and issue pages (HTML). `IssueComparer` orders "2026-44" style issue ids. |
| `FederalRegisterClient.cs` | federalregister.gov documents.json API. Agencies: IRS, Treasury, FinCEN. |
| `FeedClient.cs` | `FeedSource` (name, URL, optional category filter, enabled, built-in) and `FeedSource.Defaults`. Reads RSS 2.0 and Atom, and pages back through WordPress feeds. `DiscoverAsync` finds a site's feed for the Sources window. `GetItemsAsync` sends the built-in page readers below to their own classes, matched by URL. |
| `IrsNewsClient.cs` | IRS newsroom current-month page, plus last month's archive page early in a month (HTML; IR-numbered releases only). |
| `WrittenDeterminationsClient.cs` | irs.gov/written-determinations table (HTML, 100 rows a page, newest first). Rows are merged per ruling, since one ruling can be listed under several UIL codes. A ruling is kept when its UIL section or subject names a core section. |
| `TaxCourtClient.cs` | The Tax Court's public DAWSON opinion search (JSON). One full-text query per run, for opinions filed since the bookmark: `"section 6048" \| "sec. 6048" \| "Form 3520" \| …`. |
| `TreasuryPressClient.cs` | The per-year JSON list behind home.treasury.gov's press-release page. It keeps tax headlines and headlines naming a core term. |
| `ProfileTerms.cs` | Turns the profile into search terms. It takes the buckets weighted 2 or 3 and splits their terms into IRC sections (ranges and regulation cites expanded) and other phrases (bare form numbers become "Form N"). Used only by the three narrowed readers above. |

All the page readers have a static `Parse(...)` that takes the raw page or JSON. That's what the
tests call with saved fixtures.

**Scoring**

| File | What it holds |
|---|---|
| `Scoring.cs` | `IScorer`; `ScoringPrompt` (the system prompt with the profile inlined, the JSON payload builder, the output JSON schema, `ParseResult`, which clamps scores to 0–3). This prompt is the one place that defines how items are scored. |
| `ClaudeCodeScorer.cs` | Runs `claude -p` (the Claude Code CLI) in an empty temp folder with `--json-schema`, `--setting-sources ""`, and only the WebSearch tool allowed. It uses the user's Claude subscription. This is the default scorer. `Locate` finds claude.exe on PATH or in the WinGet folders. |
| `ClaudeScorer.cs` | The same prompt through the Anthropic API (SDK), with a key. |
| `OpenAiCompatibleScorer.cs` | Any `/chat/completions` endpoint: OpenAI, Gemini, OpenRouter, or a local Ollama / LM Studio server. `ScoringBatches` can split a run into several requests (the `ItemsPerRequest` setting, for small local models) and merges the results. |
| `ApiKeyStore.cs` | API keys encrypted with Windows DPAPI (current user) in `%APPDATA%\RegWatch`. The Anthropic key can also come from `ANTHROPIC_API_KEY`. |

**Profile, settings and smaller pieces**

| File | What it holds |
|---|---|
| `ProfileDocument.cs` | Parses `PROFILE.md` into editable buckets (`- **key (weight)** — term, term`) and guards. Everything else is kept as verbatim text, so saving never reformats what wasn't edited. Used by the profile editor and `ProfileTerms`. |
| `AppSettings.cs` | `%APPDATA%\RegWatch\settings.json`: scorer choice and models, OpenAI-compatible settings, feeds, theme, notifications, feedback toggle. `EffectiveFeeds` adds built-in sources that are new since the file was saved, matched by URL. `ScorerFactory` builds the configured scorer, or says why it can't (`Problem`). |
| `Feedback.cs` | `feedback.jsonl`: "too high / too low / not relevant" marks from the digest view, one per item. |
| `ReadMarks.cs` | `read.json`: which digests have been opened. |
| `WeeklyTask.cs` | Task Scheduler XML: Mondays 19:00, StartWhenAvailable, run as the signed-in user. |
| `UpdateCheck.cs` | Reads the GitHub releases list and compares semantic versions, pre-releases included. |
| `AppInfo.cs` | `Version` (numeric) and `DisplayVersion` (with the `-alpha` label). |

## App (WPF)

Uses the .NET 10 Fluent theme (`Application.ThemeMode`; `Theme.cs` sets the colour tone
resources). Colours in XAML come from theme resources (`DynamicResource`), not literals, so light
and dark both work.

| Window | Purpose |
|---|---|
| `App.xaml.cs` | Startup. Applies the theme, resolves the data folder, and on first run offers to create it from `Defaults/PROFILE.md`. A folder named with `--root` or `REGWATCH_ROOT` that has no profile is an error, not a setup offer. |
| `MainWindow` | The digest list (search, filter, unread dots), the digest view, comment deadlines, the run log, and the Settings ▾ and Help ▾ menus. Run now runs the pipeline on a background task. Schedule weekly registers `WeeklyTask` through `schtasks /Create /XML`. |
| `MarkdownView.cs` | Renders Markdown into WebView2 with Markdig. Two pipelines: digests (line breaks kept) and documents. Injects the script that adds the feedback buttons to each card (clicks come back as web messages) and highlights search matches. Links open in the default browser. Light/dark comes from the WebView2 profile's colour scheme. |
| `ProfileEditorWindow` | Card editor for buckets and guards, a raw-text tab, and import/export. Import keeps the old profile as `PROFILE.backup-<timestamp>.md`. |
| `ScoringSettingsWindow` | Scorer settings, API keys, presets, Test connection. |
| `SourcesWindow` | Turn sources on and off, set category filters, add a feed by site address. |
| `FeedbackWindow`, `HelpWindow` | Review or remove feedback marks; the user guide (`docs/GUIDE.md`, embedded as a resource). |

## Runner (console)

`RegWatch.Runner.exe [--root <folder>] [--dry-run] [--no-notify]` is what the scheduled task runs.
Exit codes: 0 ok, 1 run failed, 2 scorer not ready. It appends to `logs\<date>.log` and, unless
notifications are off, shows a Windows notification (`Notifier.cs`, a tray balloon on its own STA
thread). Clicking the notification opens the app.

## Where data lives

| Place | Contents |
|---|---|
| Data folder (`%USERPROFILE%\RegWatch` by default) | `PROFILE.md`, `STATE.json`, `digests\`, `logs\`, `feedback.jsonl`, `read.json`. Everything a run reads or writes. |
| `%APPDATA%\RegWatch` | `settings.json` and the DPAPI-encrypted key files. |
| `%LOCALAPPDATA%\RegWatch\WebView2` | The WebView2 cache. |

What leaves the machine: requests to the public government sites and feeds above, and the
fetched public text plus the profile sent to the chosen scorer. No client data is involved. The
profile describes topics, not people.

## Tests

`dotnet test RegWatch.slnx` runs 108 xUnit tests in a few seconds. None of them touches the
network.
- Parsers are tested against saved real pages in `tests/RegWatch.Tests/Fixtures` (IRB, Federal
  Register, feeds, IRS newsroom, letter-ruling table, Tax Court JSON, Treasury JSON). The fixtures
  have been trimmed, and one letter-ruling row is invented, as a comment in that file says.
- HTTP is faked with small `HttpMessageHandler` subclasses inside the test files.
- Scorers are tested on request building and response parsing. The Claude CLI, the Anthropic API
  and a real OpenAI-compatible server have not been tested automatically.
- The WPF windows have no automated tests. They were checked by hand and with UI Automation
  scripts that aren't in the repo.

When a source's page layout changes, save a fresh copy as the fixture, adjust the `Parse` regex
or JSON reader, and the existing tests show what still holds.

## Build, install, release

```
dotnet build RegWatch.slnx -c Release
dotnet test  RegWatch.slnx -c Release
installer\build.ps1        # needs Inno Setup 6; writes installer\Output\RegWatch-Setup-<version>.exe
```

- The installer is per-user by default (an all-users option is offered). It checks for the .NET 10
  Desktop Runtime. On uninstall it removes the scheduled task only if that task points at this
  copy's Runner.
- **CI** (`ci.yml`) runs the tests on every push to main and every pull request.
- **Release** (`release.yml`) runs on a `v*` tag. The tag must equal `v` + `<Version>`; it runs the
  tests, builds the installer, and creates a GitHub release (a pre-release when the version has a
  label). The app's Help → Check for updates reads those releases. It has not been run yet.

## Adding a source

1. Write a static reader in Core, modelled on `IrsNewsClient` or `TreasuryPressClient`:
   `Handles(url)`, `GetItemsAsync(...)` returning `FeedItem`s with a stable `Id` and a
   `Published` date, and a `Parse(...)` that takes the raw page or JSON.
2. Add a branch in `FeedClient.GetItemsAsync` and an entry in `FeedSource.Defaults`. Existing
   users get it through `EffectiveFeeds`.
3. Save a fixture and add tests (see `SourceReaderTests.cs`).
4. If the source isn't commentary, update how `ScoringPrompt` and `DigestWriter` describe it (see
   the known issues below).

Before adding a site, check it allows automated reading. Tax Notes and irs.gov/downloads/irs-wd
answer HTTP 403, and RegWatch respects that rather than disguising its requests.

## Known issues (code review, 28 October 2026)

Open; none fixed yet:
- **Digest viewer link handling.** Markdig passes raw HTML through, and links in fetched content
  are opened with the Windows shell whatever their scheme. It should disable raw HTML and open
  only http(s) links.
- **Rulings, Tax Court opinions and Treasury releases are described as commentary** in the scoring
  prompt and the digest's "In the press" note, although opinions and rulings are primary sources.
- **Cancel while sources are being read** is treated as a per-source fetch problem, so a run with
  nothing else new can still write a digest and save `STATE.json`.
- **A profile term with a parenthetical** ("8975 (+ Schedule A, …)") never becomes "Form 8975" in
  `ProfileTerms`.
- **settings.json is not written atomically**, and an unreadable file silently loads as defaults,
  so the next save loses the user's settings.
- **No lock between a scheduled run and Run now**, so both can run at once.
- **Narrowed sources only advance their bookmark when something is kept**, so quiet weeks
  re-read a growing window.
- Smaller: `PROFILE.md` is read twice per run; `ClaudeCodeScorer` writes stdin before draining
  stdout/stderr; the section regex's case-insensitive letter can swallow a following lowercase
  letter.

Also open (see `BACKLOG.md`): a real test of the API-key and OpenAI-compatible scorers, and a
licence decision. `LICENSE` currently allows evaluation only.
