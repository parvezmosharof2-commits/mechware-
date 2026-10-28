# REVIEW, QA and CONTROL PROMPTS

> Use these between phases and whenever output quality drops. Real agencies charge
> USD 100,000 because of this review discipline, not only because of the first draft.

---

## R1 — Phase Sign-off Review (after every phase)

```text
Act as three strict reviewers in sequence — a principal engineer, a senior product
designer from a top agency, and a QA lead. Review everything built in the current phase
against docs/01_PROJECT_BRIEF.md, docs/02_DESIGN_SYSTEM.md and the phase's acceptance criteria.

1. Requirements: make a table of every requirement and acceptance criterion of this phase
   with status (Done / Partial / Missing) and evidence (file, test, or screenshot).
2. Code: architecture boundaries respected? business logic only in services? any `any`,
   duplicated logic, missing validation, missing error handling, N+1 queries, missing indexes,
   secrets or config hard-coded?
3. Design: open the app in the browser at 360, 768, 1280 and 1920 px, light and dark, EN
   and BN. Check against the Visual QA checklist (design system section 8). Take screenshots.
4. Tests: run all tests; list untested business rules and add tests for them.
5. Then FIX every Partial/Missing item and every issue found, re-run checks, and report
   a final table with before/after evidence. Do not mark anything Done without evidence.
```

## R2 — Premium Design Critique (run on every key page)

```text
Look at [PAGE NAME] like a design director deciding whether this is worth USD 100,000.
Compare it side by side with the equivalent page on Amazon, Apple Store and one top
Shopify Plus brand. Score it 1–10 on: visual hierarchy, typography, spacing rhythm,
imagery quality and consistency, colour discipline, micro-interactions, information
density, trust signals, mobile ergonomics, copywriting.

For every score below 9, list the exact problems (element, what is wrong, why it hurts
conversion or perceived value) and the exact fix using our design tokens. Implement all
fixes and show before/after screenshots. Remove anything that looks like a generic template.
```

## R3 — Security Review

```text
Act as a penetration tester. Review the API and frontends for OWASP Top 10 and ASVS L2
issues: broken access control/IDOR (try every endpoint as another user, another seller,
and anonymous), price and quantity tampering, coupon abuse (reuse, stacking, race
conditions), payment webhook spoofing and replay, auth (token theft, refresh reuse,
brute force, OTP abuse), injection, XSS in reviews/product descriptions/rich text, SSRF in
image URLs, file upload abuse, CSRF, open redirects, secrets in bundles or logs,
missing rate limits, verbose errors. For each finding: severity, proof, fix, and an
automated regression test. Fix all High and Critical findings now.
```

## R4 — Performance Review

```text
Run Lighthouse (mobile, throttled) on home, category, PDP, cart and checkout, and a bundle
analysis for apps/web. For each page report LCP, INP, CLS, TBT, JS size, and the top 5
causes of slowness with fixes. Check API p95 for the 20 most used endpoints, Redis hit
rate and slow queries (EXPLAIN ANALYZE). Implement the fixes and report before/after numbers.
```

## R5 — Bug Report Template (when something is broken)

```text
BUG: [short title]
Where: [page / endpoint / role]
Steps to reproduce:
1. ...
2. ...
Expected: [what should happen per the brief]
Actual: [what happens; paste error / screenshot]

Find the root cause (not just the symptom), explain it in 2–3 sentences, fix it, add a
regression test that fails before the fix and passes after, and check whether the same
bug pattern exists anywhere else in the codebase.
```

## R6 — Change Request Template (when you want something different)

```text
CHANGE REQUEST: [title]
Current behaviour: ...
Desired behaviour: ...
Why (business reason): ...
Reference (link or screenshot of a site that does it well): ...

First, update docs/01_PROJECT_BRIEF.md or docs/02_DESIGN_SYSTEM.md so the docs stay the
source of truth. Then list the impacted modules, files and tests, implement the change,
and update tests and PROGRESS.md.
```

## R7 — Context Refresh (when the agent forgets rules or a new session starts)

```text
New session. Before doing anything, re-read docs/01_PROJECT_BRIEF.md,
docs/02_DESIGN_SYSTEM.md, docs/ARCHITECTURE.md and PROGRESS.md. Summarise in 10 bullet
points: the stack, the architecture rules, the design rules, the current phase, what is
done, what is next, and any open issues. Then continue with the next unchecked task in PROGRESS.md.
```

## R8 — Final "USD 100,000 Polish" Pass (before launch)

```text
Final polish pass across the entire storefront, console and emails. Go page by page and:
- Unify spacing, typography and icon sizes to the tokens; fix every misalignment.
- Make every image consistent (ratio, background, crop) and every empty/error state illustrated and helpful.
- Rewrite weak copy to be clear, confident and benefit-driven, in both EN and BN.
- Add the missing delight: smooth page transitions, add-to-cart feedback, skeleton
  shimmer, success animations, hover details — all within the motion rules.
- Check trust signals on PDP, cart and checkout.
- Check every flow on a real mid-range Android phone viewport over throttled 4G.
Produce a final screenshot gallery of all key pages (mobile + desktop, light + dark).
```
