# RegWatch — Weekly Run Playbook

The scheduled agent runs this every week. Keep it deterministic; put judgment in
`PROFILE.md`.

## 0. Load config
- Read `PROFILE.md` (buckets, rubric, guards).
- Read `STATE.json` → `last_irb_issue`, `last_fedreg_date`.

## 1. Source A — Internal Revenue Bulletin
- Fetch `https://www.irs.gov/irb` and list issues newer than `last_irb_issue`.
- For each new issue, fetch `https://www.irs.gov/irb/<issue>_irb`
  (e.g. `2026-26_irb`) and extract every item: citation, IRB Part,
  IRC sections, and the "Highlights of This Issue" blurb.

## 2. Source B — Federal Register (IRS + Treasury)
- Fetch (one line):
  `https://www.federalregister.gov/api/v1/documents.json?conditions[agencies][]=internal-revenue-service&conditions[publication_date][gte]=<last_fedreg_date>&order=newest&per_page=100&fields[]=document_number&fields[]=type&fields[]=title&fields[]=abstract&fields[]=action&fields[]=publication_date&fields[]=effective_on&fields[]=comments_close_on&fields[]=html_url&fields[]=regulation_id_numbers`
- Repeat with `conditions[agencies][]=treasury-department`.
- Optional (foreign-account angle): also `conditions[agencies][]=financial-crimes-enforcement-network`
  for FBAR / FinCEN items.
- If `count > per_page`, page with `&page=2`, `&page=3`, …

## 3. Merge & dedup
- Combine A + B. Dedup by document number / citation (Rev. Rul., Rev. Proc.,
  Notice, T.D./TD, REG-, IR-, FR doc number). The same item often appears in both.

## 4. Score
- Score every item 0–3 per `PROFILE.md`. Record matched bucket(s), a one-line
  "why you care", and (for proposed rules) the `comments_close_on` date.
- Before scoring any **final rule** (not proposed rules or routine PRA notices)
  a 0 or 1 based on its Federal Register `abstract` alone, do one quick
  WebSearch for news coverage of the rule/document number. The `abstract`
  field can describe only part of a multi-part holding — e.g. the 2026-08-14
  FinCEN BOI/CTA rule's abstract mentioned only the "US person exemption" and
  omitted the more consequential "reporting company redefined to exclude
  domestic entities" holding, which caused a same-day mis-score that had to be
  corrected. This applies especially to Treasury/FinCEN final rules touching
  ownership-transparency, AML, or entity-registration regimes.

## 5. Write the digest
- Write `digests/<YYYY-MM-DD>.md`, sorted high→low score, card format below.
- Collapse all score-0 items into one bulleted "Skip" list (cite + ~5-word reason).
- If nothing scores ≥ 2, say so plainly at the top.

## 6. Update state & report
- Update `STATE.json`: `last_irb_issue` = newest issue seen; `last_fedreg_date`
  = today (YYYY-MM-DD).
- Print a 3–5 line run summary: counts by score + the single top item
  (or "nothing above score 1 this week").
- If any fetch failed, note it in the digest rather than failing silently.

## Card format
```
### <🔴|🟡> <CITE> — relevance <score> (<bucket>)
**Type:** <Rev Rul / Notice / Proposed Rule / TD / …> · **IRC:** <§§> · **Comment deadline:** <date|n/a>
**Source:** <url>
**Plain English:** <one sentence>
**Why you care:** <matched bucket + reason>
```

## Guardrails
- Summaries are **triage, not authority.** Always include the exact cite and the
  primary-source link; never restate a holding as advice.
- Never invent a cite, date, or section number. If the blurb doesn't say it,
  write "not stated".
