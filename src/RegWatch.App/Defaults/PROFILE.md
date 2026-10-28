# RegWatch — Topic Profile & Scoring Rubric

**This is the only file you edit to calibrate the digest.** The weekly agent
re-reads it every run. Change weights, add/remove buckets, add signal terms or
false-positive guards — the playbook and the schedule never need to change.

Owner focus: US foreign-trust & international tax compliance.

## Scoring scale (0–3)

- **3 — Read now.** Direct hit on a core bucket (foreign trusts, foreign gifts,
  international information returns). The kind of item that changes a filing.
- **2 — Read this week.** International tax generally, treaties, FATCA,
  anti-deferral, FTC, expatriation — relevant but not core-critical.
- **1 — Skim / reference.** Recurring numbers that touch your work (AFRs incl.
  §7520, inflation adjustments) — grab the figure and move on.
- **0 — Skip.** Out of scope.

## Topic buckets

Score an item to the highest-matching bucket's base score; subtract 1 if the
mention is incidental. Never raise above 3.

- **foreign_trusts (3)** — §679, §672(f), §6048, §6677, §643(i), §665–668,
  Form 3520, Form 3520-A, foreign grantor trust, throwback, accumulation
  distribution, UNI, DNI, foreign nongrantor trust
- **foreign_gifts (3)** — §6039F, gift or bequest from a foreign person,
  Form 3520 Part IV
- **intl_info_returns (3)** — Form 5471, 5472, 8865, 8938, 8621, 926, 8975
  (+ Schedule A, country-by-country report), §6038, §6038A, §6038D, §6046, §1.6038-4
- **anti_deferral (2)** — Subpart F, GILTI, §951A, §965, §250, FDII, PFIC,
  §1291, §1297, §1298, QEF, mark-to-market election
- **foreign_tax_credit (2)** — §901, §902, §904, §905, §960, foreign tax
  credit, high-tax exception
- **fatca_withholding (2)** — FATCA, chapter 4, chapter 3, §1471–1474, FFI,
  Form 8966, 1042, 1042-S, W-8BEN, §1441, §1446
- **treaties (2)** — tax treaty, income tax convention, competent authority,
  §894, totalization agreement, IRB Part II
- **expatriation (2)** — §877A, §877, Form 8854, covered expatriate, §2801
- **foreign_corp_partnership (2)** — §367, §1248, CFC, §958, §7874 inversion,
  international §318 attribution
- **penalties_relief_intl (2)** — §6677, §6038 penalty, streamlined filing,
  DIIRSP, reasonable cause for international info returns, voluntary disclosure
- **estate_trust_valuation (1)** — §7520 rate, AFR, §1274, §7872, §2031
  (note: §7520 drives GRAT / CLAT / life-estate valuations)

## False-positive guards (do NOT score up on the term alone)

- "nonbank trustee" / "trustee of an individual retirement account" → §408 IRA
  custodian rules, not fiduciary/foreign trust → **0**
- "trust fund recovery penalty" / §6672 → payroll withholding, not fiduciary → **0**
- "real estate investment trust" / "REIT" → **0** unless a foreign-holder /
  FIRPTA angle is explicit
- "Agency Information Collection Activities" / Paperwork Reduction Act notices
  (Federal Register) → **0** by default. If the form or topic is in a bucket
  above (e.g. Form 3520, Form 8975, §7701 trust residence), **cap at score 1
  (skim)** — these are routine OMB form-burden renewals, not new law. List them
  with their comment deadline, but never score a PRA notice 2 or higher.
- "hearing" / "hearing cancellation" notices → **0** unless the underlying reg
  is a bucket hit
- "grantor" outside a trust context → ignore

## Scoring instructions for the agent

1. Apply the false-positive guards BEFORE assigning any keyword-driven score.
2. Match to the highest-scoring bucket; record which bucket(s) matched.
3. Subtract 1 for incidental/passing mentions; a plain "international" item with
   no specific bucket = 2.
4. For each item output: score, matched bucket(s), and a one-line "why you care".
5. Capture comment deadlines (Federal Register `comments_close_on`) for any
   proposed rule — these are high-value even at score 2.

_Last calibrated: 2026-06-12 (initial profile, derived from the setup spike). Edit freely._
