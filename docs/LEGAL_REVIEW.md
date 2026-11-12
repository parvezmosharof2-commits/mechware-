# Legal review hand-off (H1.2)

All seven legal pages are **drafts**. They show the notice "Draft — this text is pending legal review and is not yet in force." and are `noindex` and left out of the sitemap until approved. Texts use only facts stated in `docs/01_PROJECT_BRIEF.md`; everything else is marked `[to be confirmed]` (Bangla: `[নিশ্চিত করা হবে]`).

**How to replace a text:** edit the JSON file (`frontend/apps/web/src/content/legal/<slug>.<en|bn>.json`): `title`, `intro`, `updated` (ISO date, shown as "last updated"), `sections[]` with `h` (heading), `p` (paragraphs), `l` (bullets), `after` (paragraphs below the bullets). No code change. When all pages are approved set `LEGAL_APPROVED = true` in `frontend/apps/web/src/content/legal/index.ts`: this removes the draft notice and `noindex` and adds the pages to `/sitemaps/pages.xml`. The help centre FAQ (`Help` namespace in `src/messages/{en,bn}.json`) repeats some of these facts; keep it in step.

| Page (URL `/legal/...`) | Files | Brief facts used |
|---|---|---|
| terms | `terms.en.json`, `terms.bn.json` | MECHWARE sells directly (own and authorised-distributor stock) §2; BDT; VAT per category, default 5%, VAT-inclusive for guests, exclusive with VAT line for companies, server-side calculation §8.1; payment confirmed only by server verification, 30-minute auto-cancel of pending online payments §8.6/8.7; payment methods list §3.1 item 10; COD §8.7 |
| privacy | `privacy.*.json` | PDPO 2025: explicit, purpose-specific, revocable consent with records; bilingual notice; export, correction, deletion/anonymisation requests; tax records kept as law requires §14; company profile fields (trade licence, BIN, TIN) §3.1; card data never on our servers §8.7 |
| cookies | `cookies.*.json` | Consent-aware analytics, no marketing tags before consent §13; sign-in marker cookie and language cookie (from the app code) |
| returns | `returns.*.json` | Default 7 days after delivery, per-product policy, opened/installed electrical parts non-returnable unless faulty, RMA with photos, refund to original method or credit note §8.10 |
| shipping | `shipping.*.json` | Zones, base + per-kg chargeable weight, heavy items quote-only §8.9; 5 / 10 days from confirmation and 48 h courier hand-over per Digital Commerce Operation Guidelines 2021 §8.6; COD serviceable areas only §8.7 |
| credit | `credit.*.json` | Net 15 / net 30, approved companies, limit set by MECHWARE finance, limit check on order §3.1/8.7 |
| quote-terms | `quote-terms.*.json` | Default validity 15 days, expired quote rejected, accept creates order at quote prices with stock re-check, every change a new immutable version §8.2; export quote-only in USD §2 |

## "[to be confirmed]" items

**terms:** legal entity name, trade licence and registration details; BIN; VAT rate confirmation (5% default per category); which payment methods are open at launch; governing law, court and complaint contact.

**privacy:** full list of personal-data categories; when export / correction / deletion requests become available; retention periods other than tax records; list of processors and any transfer outside Bangladesh; data protection contact.

**cookies:** analytics tools used, cookie names and lifetimes; how to change or withdraw consent.

**returns:** refund deadline (brief §8.6 says counsel must confirm); warranty terms.

**shipping:** delivery rates per zone; maximum order value for cash on delivery (brief: configurable, default 15,000 BDT).

**credit:** approval criteria; late-payment charges and consequences.

**quote-terms:** further terms for export quotes.

**help centre (`/help`):** delivery rates and refund deadline (same as above). The contact section shows "Contact details coming soon." (office address, phone and email appear in the brief §2 but the owner has not released them for the site). Payment methods and quotes / credit / VAT invoices are shown as "Coming soon" until built.

Also pending from the brief (§14): a Bangladeshi lawyer and a VAT consultant must sign off every item above before launch.
