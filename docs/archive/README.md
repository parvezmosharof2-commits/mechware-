# Archive

এখানকার কিছুই এখন আর অনুসরণ করা হয় না। শুধু ইতিহাস আর সিদ্ধান্তের পটভূমির জন্য রাখা (২০২৬-১১-০৫)।
এখনকার প্ল্যান: [`docs/MASTER_PLAN.md`](../MASTER_PLAN.md)।

| ফাইল | কী ছিল | কেন বাতিল |
|---|---|---|
| `prompt-pack-v1/` | প্রথম prompt pack (brief v1, design system, phase prompts, review prompts, guideline) | brief v2 ও `docs/`-এর নতুন সংস্করণ এর জায়গা নিয়েছে; v1 ছিল B2C marketplace ধাঁচের |
| `MASTER_EXECUTION_GUIDE.md` | ২১ দিনের Antigravity + Gemini 1.5 Pro ফ্লো | `MASTER_PLAN.md` §২–§৩ |
| `STEP_BY_STEP_NOTE*.md`, `WORKFLOW_NOTE.md`, `CODE_TEST_FLOW.txt` | পুরনো দৈনিক কাজের নোট | `MASTER_PLAN.md` §২ (প্রতিটা কাজের ৯ ধাপ) |
| `review-phase0-4.md` | Phase 0–4-এর পুরনো অডিট | ফলাফল কোড ও `PROGRESS.md`-এ; পরের রিভিউ `docs/reviews/` |
| `ARCHITECTURE_v1.md` | প্রথম আর্কিটেকচার (B2C marketplace, Stripe, AWS SNS, Gemini, seller ledger) | `docs/ARCHITECTURE.md` (v2, কোড আর brief v2 অনুযায়ী) |
| `qa-phase3/visual-qa-report.json` | Phase 3-এর ভিজ্যুয়াল QA রিপোর্ট | রিপোর্টটা পুরনো AI Studio Vite অ্যাপের (এখন মোছা) পেজের; এখনকার UI যাচাই `frontend/packages/e2e` করে |

মোছা হয়েছে (git ইতিহাসে আছে): root-এর পুরনো AI Studio Vite অ্যাপ (`src/`, `index.html`, `vite.config.ts`,
`package.json`, `bun.lock`, `metadata.json`, `public/assets/aistudio/`) আর তার QA capture script।
