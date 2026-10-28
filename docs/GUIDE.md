# RegWatch user guide

RegWatch reads each week's new IRS and Treasury guidance, scores every item 0–3 for a US
foreign-trust and international tax practice, and writes a digest you can skim in a minute.

> **Alpha software.** RegWatch is a triage tool, not tax advice and not an authority. Every
> card links to the primary source; read that before relying on anything for a client.

## Getting started

1. **Install.** Run `RegWatch-Setup-<version>.exe`. It needs the free **.NET 10 Desktop Runtime
   (x64)** from Microsoft; setup tells you if it is missing and opens the download page.
   The alpha installer is not code-signed, so Windows may say **"Windows protected your PC"**:
   click **More info**, then **Run anyway**.
2. **First start.** RegWatch offers to create its data folder, `%USERPROFILE%\RegWatch`, with
   the default profile. Click OK.
3. **Choose who scores the items.** Open **Settings → Scoring**:
   - **Claude subscription (Claude Code)** (default): uses your Claude Pro or Max plan through
     the `claude` command-line tool, with no per-run cost. Install Claude Code, then sign in once
     in a terminal with `claude auth login`.
   - **Anthropic API key:** billed per run, usually well under a dollar. Set the key under
     **Settings → Advanced scoring settings…**.
   - **OpenAI-compatible / local AI:** OpenAI, Gemini, OpenRouter, Ollama, LM Studio or any
     OpenAI-style server. There is no web search, so the final-rule news check is skipped, and
     small local models may score borderline items differently.

   **Advanced scoring settings…** also has a **Test connection** button that scores one sample
   item without changing anything.
4. **Click Run now.** The first run reads the latest 4 Internal Revenue Bulletins and the last
   30 days of the Federal Register. Later runs pick up where the last one stopped.

## What a run does

- Reads the **Internal Revenue Bulletin** ("Highlights of This Issue" for each new issue) and the
  **Federal Register** (IRS, Treasury and FinCEN documents, including comment deadlines).
- Reads the **commentary feeds** and **IRS news releases** under **Settings → Sources…** (The Tax
  Adviser, the Journal of Accountancy's tax section and the IRS newsroom's IR- releases are built
  in). Only headlines, short summaries and links are kept.
- Sends that public text, plus your profile and your latest scoring marks (see below), to the
  scorer you chose; it scores everything in one pass.
- Writes the digest to `digests\<date>.md` and remembers where it stopped in `STATE.json`, so
  nothing is read twice or missed.

The **Run log** tab shows progress. **Cancel**, or a failed run, leaves `STATE.json` unchanged,
so the next run simply covers the same window again.

## Reading the digest

| Score | Meaning |
|---|---|
| **3, read now** | Direct hit on a core topic: foreign trusts, foreign gifts, international information returns. The kind of item that changes a filing. |
| **2, read this week** | International tax generally: treaties, FATCA, anti-deferral, foreign tax credit, expatriation. |
| **1, skim** | Recurring figures that touch the work (§7520 rate, AFRs), and routine form-burden notices. |
| **0, skip** | Out of scope. |

- The list on the left shows each digest with a badge: **3× 3** means three items scored 3;
  **2× 2** means no 3s but two 2s; **–** means nothing above 1. A dot marks a digest you have
  not opened yet.
- **Search digests** (above the list) finds every digest that mentions a word, IRC section or
  form number, and highlights it in the open digest. The box under it narrows the list to unread
  digests, or to digests where something scored 3, or 2 or 3. Right-click the list to mark
  everything as read.
- Articles and IRS news releases appear under **In the press**, linked to the IRB or Federal
  Register item they discuss.
- **Comment deadlines** lists upcoming comment deadlines from every digest. Double-click a row to
  open the Federal Register page.

## Marking a score as wrong

Hover over an item in a digest to see **Too high**, **Too low** and **Not relevant** (skipped
items only offer **Too low**). Click one to record your view; click another to change it.

- Your latest 40 marks go to the scorer with the profile as calibration examples, so similar
  items are scored the way you indicated. Turn this off, review the marks, or remove some under
  **Settings → Scoring feedback…**.
- Marks are also a to-do list for the profile: a pattern of "Not relevant" usually wants a
  guard, and "Too low" a missing term.
- They are kept in `feedback.jsonl` in the data folder.

## Tuning the scoring: Edit profile

Everything RegWatch knows about your practice is in `PROFILE.md`, which **Edit profile** shows as:

- **Topic buckets:** each has a weight (3, 2 or 1) and the terms that signal it: IRC sections,
  forms and phrases. An item scores the weight of the best bucket it matches, minus 1 for a
  passing mention.
- **False-positive guards:** terms that look relevant but are not, for example "nonbank trustee"
  (an IRA custodian rule, not a foreign trust) or Paperwork Reduction Act notices (capped at 1).
- **Raw text:** the whole file, for anything the cards do not cover.

Too noisy? Add a guard or lower a weight. Missing things? Add terms or a new bucket. The next run
uses the changes; nothing else needs updating.

**Export…** saves a copy of your profile to share with a colleague; **Import…** replaces yours
with one you received. Your current profile is backed up first as `PROFILE.backup-<date>.md`.

## Running it every week

**Schedule weekly** creates a Windows scheduled task that runs RegWatch every Monday at 19:00
(after the US-morning IRS release). It runs as you, while you are signed in to Windows. If the
computer is off or asleep at 19:00, the run happens as soon as possible afterwards. Each run
writes a log to `logs\<date>.log` in the data folder. Click it again to update the task.
Uninstalling RegWatch removes the task.

When a scheduled run finishes, a Windows notification says how many items scored 3 and 2; click
it to open RegWatch. Turn this off with **Settings → Notify after scheduled runs**.

## Settings

| Menu item | What it does |
|---|---|
| **Scoring** | Who scores the items (see Getting started). |
| **Advanced scoring settings…** | Models, API keys, OpenAI or local server details, connection test. |
| **Scoring feedback…** | Your Too high / Too low / Not relevant marks, and whether they go to the scorer. |
| **Sources…** | Turn commentary feeds on or off, limit them to categories, or add any site with an RSS/Atom feed. Some sites (Tax Notes, for example) block automated readers and cannot be added. |
| **Notify after scheduled runs** | Windows notification when the weekly run finishes. |
| **Theme** | Match Windows, Light or Dark. |

## Where things are kept

| What | Where |
|---|---|
| Profile, digests, run state, logs | `%USERPROFILE%\RegWatch` (**Open folder** goes there) |
| Settings | `%APPDATA%\RegWatch\settings.json`, for your Windows account only |
| API keys | `%APPDATA%\RegWatch`, encrypted for your Windows account (DPAPI) |

**What leaves your computer:** RegWatch downloads public pages from irs.gov, the Federal Register
and the feeds you enable. The scorer receives that public text and your `PROFILE.md`. No client
information is involved at any point.

## Troubleshooting

- **"Claude Code (claude.exe) was not found":** install Claude Code, or point to `claude.exe`
  under **Settings → Advanced scoring settings…**.
- **A run says the Claude session expired:** run `claude auth login` in a terminal, then **Run now**.
- **A run failed part-way:** read the **Run log**. `STATE.json` was not changed, so just run again.
  An IRB issue that could not be fetched is retried next time.
- **Scores look off with a local model:** the profile was calibrated against Claude. Compare a few
  digests, or use **Test connection**: the sample item should score 1.

## Version and feedback

**Help → About RegWatch** shows the version and folder locations. Press **Ctrl+C** in that box to
copy the details into an email or bug report. **Help → Check for updates…** looks for a newer
release on GitHub. While the repository is private, GitHub only shows releases to signed-in
collaborators, so the check may send you to the Releases page in your browser instead.
