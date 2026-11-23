# RegWatch — session handoff (2026-10-28)

Read this first in a new session, then `README.md` and `BACKLOG.md`.

## Where things stand

RegWatch has moved from a scheduled Claude agent to a local .NET 10 WPF app (moved up from .NET 8 on 2026-10-28). Everything is
committed on `main`, **pushed to GitHub** (`origin/main`). The `local-app` branch is
merged and deleted.

| Commit | What |
|---|---|
| `5dc03e6` | App skeleton: WPF viewer, headless runner, IRB + Federal Register fetchers, Claude scoring |
| `4872059` | Scoring via Claude subscription (`claude -p`) by default; API-key mode kept |
| `976988e` | First app-run digest `digests/2026-10-28.md` + `STATE.json` |
| `e601659` | In-app PROFILE.md editor (bucket cards, guards, raw text) |
| `71b361c` | OpenAI-compatible / local AI scoring + Advanced… settings window |
| `db0bec4` | Commentary feeds (The Tax Adviser, JofA TAX, custom) + "In the press" digest section |

- Published build: `%USERPROFILE%\Desktop\RegWatch` (`RegWatch.App.exe`, `RegWatch.Runner.exe`).
- Tests: 65 xUnit tests, all passing (`dotnet test RegWatch.slnx`).
- This user's scoring setting: **Claude subscription (Claude Code, opus)**, in
  `%APPDATA%\RegWatch\settings.json`. No Anthropic API key exists on this machine.
- The `claude` CLI is signed in to the user's **Pro** plan (`claude auth status`). Its session
  can expire; the run log then says to run `claude auth login`.

## Scheduling — nothing is scheduled right now

- The old Claude scheduled task `regwatch-weekly-digest` was **disabled** on 2026-10-28
  (not deleted; can be re-enabled in the desktop app).
- The Windows Task Scheduler task has **not** been created. The user creates it from the
  app's **Schedule weekly** button (Mondays 19:00, runs `RegWatch.Runner.exe --root …`).
  Until then no digest runs automatically.

## Verified vs. not verified

Verified live: IRB/FR fetching (IRB highlights complete, no truncation); scoring on the
subscription (~40 s for a light week, ~3.5 min for 58 FR docs + 7 articles); Test
connection on the subscription; feed reading, merging and bookmarks; feed discovery
(Tax Foundation found, Tax Notes correctly refused with HTTP 403); all windows on screen.

Not verified against a real server: Anthropic API-key mode (no key), OpenAI-compatible /
local AI mode (no key or local model here) — covered by unit tests with a fake server.
The "Also covered by" link has not yet appeared in a real digest (all 7 articles in the
test run were domestic and scored 0); covered by unit tests.

## Decisions made

- Tax Notes skipped: it blocks automated readers; do not try to get around that.
- Commentary keeps headline + short summary + link only, never full text.
- The OpenAI-compatible key store never reads `OPENAI_API_KEY` (so a key meant for one
  service is not sent to another).
- Re-scoring IRB 2026-43/44 matched the agent's 10/25 digest except two score-1 → 0 calls
  (Rev. Proc. 2026-20, T.D. 10055). Left to the user to calibrate via PROFILE.md if wanted.

## Working notes for the next session

- Build / publish (kill the running app first or the publish fails on locked files):
  ```
  dotnet publish src/RegWatch.App -c Release -o $env:USERPROFILE\Desktop\RegWatch
  dotnet publish src/RegWatch.Runner -c Release -o $env:USERPROFILE\Desktop\RegWatch
  ```
- Safe experiments: `RegWatch.Runner.exe --dry-run` (fetch only, writes nothing to state),
  or `--root <scratch folder>` with a copied PROFILE.md and a hand-written STATE.json.
- Files are checked out with CRLF line endings; edit scripts should preserve them.
- UI checks were done by screenshotting windows and driving buttons through Windows UI
  Automation (computer-use cannot target this unregistered app).

## Next up

The four `BACKLOG.md` items (dark mode, icon, installer, public-GitHub checklist) were done on 2026-10-28; see README for the theme, installer and icon notes. Next: work through `docs/PUBLIC-GITHUB-CHECKLIST.md` if the repo is to go public.
