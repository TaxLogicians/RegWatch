# RegWatch

Weekly triage digest of new IRS / Treasury guidance, filtered and scored for a
US foreign-trust & international tax practice.

## What it does
Every week an agent pulls new guidance from the IRB and the Federal Register (plus, in the app, the sources below), scores each item 0–3
against your topic profile, summarizes the relevant ones into cards, and writes a
dated digest you can skim in a minute.

**Sources**
- **Internal Revenue Bulletin** — irs.gov/irb (Rev. Ruls, Rev. Procs, Notices, TDs, Announcements)
- **Federal Register API** — IRS + Treasury (proposed/final regs, notices, **comment deadlines**)
- **Commentary feeds** (app only) — The Tax Adviser and Journal of Accountancy (TAX category)
  built in, plus any RSS/Atom feed added under **Settings → Sources…** (paste a site address; the app
  finds its feed). Scored like everything else but listed under **Other sources**, and linked
  to the IRB/FR card an article discusses. Only headlines, short summaries and links are kept.
  Tax Notes blocks automated readers, so it cannot be added.
- **Page readers** (app only, built in) for sources with no usable feed: IRS news releases
  (newsroom pages), IRS letter rulings (the irs.gov written-determinations table), Tax Court
  opinions (the court's DAWSON opinion search) and Treasury press releases (the JSON list behind
  home.treasury.gov's press-release page). The last three are narrowed to the IRC sections and
  terms in the profile's buckets weighted 2 or 3 before scoring (`ProfileTerms`).

## Files
- **`PROFILE.md`** — *edit this to calibrate.* Topic buckets, the 0–3 scoring
  rubric, and keyword false-positive guards.
- `PLAYBOOK.md` — the steps the agent runs each week. Rarely needs changing.
- `STATE.json` — bookmark of the last issue/date processed (so nothing repeats
  or is missed).
- `digests/` — one dated Markdown file per run. `2026-06-12_spike.md` is the
  seed example produced during setup.

## Calibrating (anytime)
Open `PROFILE.md`, adjust bucket weights / signal terms / guards, save. The next
run uses it — no need to touch the schedule. Too noisy? Raise cutoffs or add a
guard. Missing things? Add signal terms or a new bucket.

## Local app (since 2026-10-28)
`src/` holds a .NET 10 app that does the run itself, with no Claude Code session needed.
It is published to `C:\Users\dunca\Desktop\RegWatch`:
- **RegWatch.App.exe**: browse every digest (old agent-written ones too), see upcoming
  comment deadlines across all digests, use **Run now**, **Edit profile** (topic buckets
  as cards with weights and term chips, guards as a list, plus raw text; saves to
  PROFILE.md and leaves untouched entries exactly as written),
  **Schedule weekly** (Task Scheduler, Mondays 19:00), a **Settings** menu (scoring, advanced
  scoring settings with DPAPI-encrypted API keys in `%APPDATA%\RegWatch`, sources, theme) and a
  **Help** menu (the user guide, `docs/GUIDE.md` embedded in the app, also on F1; and About,
  with the version and folder locations).
- **RegWatch.Runner.exe**: the headless run that Task Scheduler calls; it logs to `logs/`.
  `--dry-run` fetches and saves the scoring payload without calling Claude or changing state.

How a run works: the app fetches each new IRB issue's "Highlights of This Issue" section
(complete, with no truncation) and the Federal Register API (IRS, Treasury, FinCEN). It
dedups by document number, including across runs via `seen_documents` in STATE.json. One
Claude call scores everything against PROFILE.md, with a web search for the final-rule
sanity check. **Settings → Scoring** picks how that call is made: **Claude subscription**
(default; runs `claude -p` and needs the Claude Code CLI signed in with `claude auth login`)
or **Anthropic API key** (`claude-opus-5-5`, billed per run), or **OpenAI-compatible /
local AI** (OpenAI, Ollama, LM Studio, Gemini, OpenRouter or any OpenAI-style server; no
web search). **Settings → Advanced scoring settings…** sets models, keys, base URL, structured-output mode, batching
for small local models, and has a Test connection button. The choice is saved per
Windows user in `%APPDATA%\RegWatch\settings.json`. The app then writes `digests/<date>.md` (plus a `.json`
sidecar) in the same card format. PROFILE.md is still the only file you edit to calibrate.

**Theme:** the app uses the Windows 11 look and follows the Windows light/dark setting.
**Settings → Theme** switches between Match Windows, Light and Dark (saved in
settings.json); the digest page follows it too.

**Installer:** `installer\build.ps1` publishes the app and runner and builds
`installer\Output\RegWatch-Setup-<version>.exe` with Inno Setup 6
(`winget install JRSoftware.InnoSetup`). Setup installs for the current user without an
admin prompt (or for all users), adds a Start menu shortcut and an uninstaller, and checks
for the .NET 10 Desktop Runtime (Program Files, `DOTNET_ROOT`, a per-user install, or `dotnet` on
PATH). Uninstalling also removes the weekly scheduled task when it runs that install's runner
(a task made from another copy is left alone), and never touches the data folder. On first start the app offers to create
`%USERPROFILE%\RegWatch` with the default profile (`src\RegWatch.App\Defaults\PROFILE.md`,
a frozen copy, so calibrating your own PROFILE.md does not change it). A folder given with
`--root` or `REGWATCH_ROOT` is never created automatically. With no STATE.json yet, the first
run reads only the latest 4 IRB issues and the last 30 days of the Federal Register. The version number lives in
`Directory.Build.props` (currently `0.1.0-alpha`; the app shows it in the title bar and
Help → About).

**Icon:** `tools\make-icon.ps1` draws it and writes `src\RegWatch.App\Assets\RegWatch.ico`.

**CI and releases:** `.github/workflows/ci.yml` runs the tests on every push to `main` and every
pull request. To publish a release, set `<Version>` in `Directory.Build.props`, commit, then push a
matching tag (`git tag v0.1.0-alpha` and `git push origin v0.1.0-alpha`). `release.yml` tests,
builds the installer and attaches it to a GitHub release (a pre-release when the version has a
label). **Help → Check for updates…** reads those releases. GitHub Actions minutes on a private repo
come out of the account's free monthly allowance, and Windows runners count double.

**Sharing the alpha:** see `LICENSE` (evaluation only, all rights reserved). Give a colleague
access by adding them as a collaborator on GitHub, or send them the installer. The installer is
not code-signed, so Windows SmartScreen warns until they click More info → Run anyway; the guide
explains this.

Build / test / publish:
```
dotnet test RegWatch.slnx
dotnet publish src/RegWatch.App -c Release -o C:\Users\dunca\Desktop\RegWatch
dotnet publish src/RegWatch.Runner -c Release -o C:\Users\dunca\Desktop\RegWatch
```

## Run it manually with the agent (original method)
From this folder:
```
claude -p "Follow PLAYBOOK.md and write this week's digest."
```

## Delivery
Currently file-based (`digests/`). To receive it by email or Slack, add a
delivery step to step 5 of the playbook (an email/Slack MCP) — easy to bolt on later.

## Important
Digests are **triage, not authority** — every card links to the primary source.
Verify before relying on anything for a client file.
