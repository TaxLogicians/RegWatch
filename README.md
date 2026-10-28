# RegWatch

Weekly triage digest of new IRS / Treasury guidance, filtered and scored for a
US foreign-trust & international tax practice.

## What it does
Every week an agent pulls new guidance from two sources, scores each item 0–3
against your topic profile, summarizes the relevant ones into cards, and writes a
dated digest you can skim in a minute.

**Sources**
- **Internal Revenue Bulletin** — irs.gov/irb (Rev. Ruls, Rev. Procs, Notices, TDs, Announcements)
- **Federal Register API** — IRS + Treasury (proposed/final regs, notices, **comment deadlines**)

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

## Run it manually
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
