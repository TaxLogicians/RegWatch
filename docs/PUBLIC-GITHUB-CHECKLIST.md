# Checklist: making RegWatch a public GitHub repo

The repo is currently **private** (`Maubec-Deeds/RegWatch`). Work through this before
changing that. Anything published stays public: forks, caches and archives keep a copy
even if the repo is made private again later.

## Already checked (2026-10-28)

- [x] **No secrets in any commit.** All history was scanned for API keys, tokens and
      passwords; none found. Keys live outside the repo (`%APPDATA%\RegWatch`, DPAPI-encrypted).
- [x] **Commit author is the GitHub no-reply address**, not a personal email.
- [x] **Nothing under `.claude/` is tracked** (`.claude/settings.local.json` is ignored).
- [x] **Committed digests contain no commentary text.** They hold only IRB / Federal Register
      material (US government works, public domain). The "In the press" section (headlines
      and short summaries from The Tax Adviser and JofA) has not appeared in a committed digest yet.

## Decide what goes public

- [ ] **Licence.** No LICENSE file exists, which means "all rights reserved": others can
      read the code but not legally reuse it. Common choices: MIT (anyone may reuse, keep
      the notice), Apache-2.0 (MIT plus a patent grant), GPL-3.0 (changes must stay open).
- [ ] **Personal files.** Remove or rewrite before publishing:
  - `HANDOFF.md`: session notes with local paths and your plan/scoring setup.
  - `README.md` lines 36 and 62–63: `C:\Users\dunca\Desktop\RegWatch` paths.
- [ ] **Your data.** `PROFILE.md`, `STATE.json` and `digests/` are your working data and
      show your practice focus. Either keep them (useful as a worked example) or move the
      data folder out of the repo (`REGWATCH_ROOT`) and keep only a generic PROFILE.md.
- [ ] **History.** The history is clean, so the visibility can simply be switched. But
      deleting a file in a new commit does not remove it from history: if any of the
      above must not be public at all, publish a fresh repo with a squashed first commit
      instead (or rewrite history with `git filter-repo`).
- [ ] **Future digests.** Once a digest includes "In the press", it holds third-party
      headlines and short summaries. Consider adding `digests/` to `.gitignore` in the public repo.

## Make it usable by others

- [ ] **README for a stranger:** what it does, the "triage, not tax advice" disclaimer,
      install (the Setup.exe and the .NET 10 Desktop Runtime), first run (creates
      `%USERPROFILE%\RegWatch`), scoring options, and how to build.
- [ ] **Third-party notices:** Markdig (BSD-2-Clause), Anthropic C# SDK (MIT),
      System.Security.Cryptography.ProtectedData (MIT), Microsoft WebView2 SDK (Microsoft
      licence, redistributable). A short `THIRD-PARTY-NOTICES.md` covers it.
- [ ] **User agent:** the app identifies itself as `RegWatch/1.0 (+local triage digest)`.
      Add the repo URL so site operators can see what is calling them.
- [ ] **Releases:** attach `RegWatch-Setup-<version>.exe` (from `installer\build.ps1`) to a
      GitHub Release rather than committing it.
- [ ] **Code signing (optional):** the installer is unsigned, so Windows SmartScreen will warn
      ("Windows protected your PC") until it builds reputation. A code-signing certificate
      removes this but costs money.

## GitHub settings once public

- [ ] Description and topics (e.g. `irs`, `tax`, `federal-register`, `wpf`).
- [ ] Turn on Dependabot alerts and secret scanning with push protection.
- [ ] Decide whether Issues / Discussions are open, and add a short `SECURITY.md`
      (how to report a problem privately).
- [ ] Protect `main` if anyone else will contribute.

## The switch

When everything above is done:

```
gh repo edit Maubec-Deeds/RegWatch --visibility public --accept-visibility-change-consequences
```
