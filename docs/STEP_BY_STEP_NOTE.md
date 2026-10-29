# MECHWARE — সহজ গাইড ও কাজের অ্যাকশন প্ল্যান (STEP-BY-STEP NOTE)

> এই ফাইলটি আপনার প্রজেক্টের প্রধান কাজের গাইড। যখনই কাজ করবেন, শুধু এই ফাইলটি ওপেন করবেন এবং দেখবেন এখন কোন ফেজ চলছে আর ঠিক কী প্রম্পট দিতে হবে।

---

## ১. প্রজেক্টের মূল তথ্য (At a Glance)

- **কোম্পানির নাম**: **MECHWARE**
- **ট্যাগলাইন**: **YOUR GATEWAY TO INDUSTRY 4.0**
- **ওয়েবসাইট টাইপ**: B2B & B2C Industrial Machinery, Automation Marketplace & Engineering Platform ($100K Agency Quality)
- **টেক স্ট্যাক**: 
  - **কোর ব্যাকএন্ড ও ফিনান্স**: C# 13 / .NET 10 ASP.NET Core Clean Architecture, Entity Framework Core, PostgreSQL 16, Redis 7
  - **ফ্রন্টএন্ড স্টোরফ্রন্ট ও অ্যাডমিন**: Next.js 15 (TypeScript, Tailwind CSS 4, shadcn/ui)
  - **ব্র্যান্ড কালার**: Tech Midnight Navy (`#060B18`), Electric Cyan / Mech Teal (`#00E5FF`), Royal Blue (`#0F3E7D`)
- **প্রজেক্ট ফোল্ডার**: `F:\CHAT GPT\WEBSIDE`

---

## ২. প্রতিটি ফেজের কাজের ৪-ধাপ নিয়ম (The Golden Loop)

যেকোনো ফেজ করার সময় সবসময় এই ৪টি ধাপ মেনে চলবেন:

```
[ধাপ ১: Antigravity-তে ফেজ প্রম্পট দেওয়া]
                     ↓
[ধাপ ২: কাজ শেষ হলে Antigravity-র সেলফ-চেক (Internal Audit)]
                     ↓
[ধাপ ৩: Google AI Studio দিয়ে ইন্ডিপেন্ডেন্ট অডিট (External Audit)]
                     ↓
[ধাপ ৪: সমস্যা ফিক্স করা ও পরবর্তী ফেজে যাওয়া]
```

---

## ৩. কখন কি করবেন: ফেজ তালিকা ও অ্যাকশন টেবিল

| ধাপ / ফেজ | কখন করবেন | কী কাজ হবে |
|---|---|---|
| **Phase 0** | **সবার প্রথমে (আজকে)** | আর্কিটেকচার ব্লুপ্রিন্ট, ডাটাবেস ER ডায়াগ্রাম, API কন্ট্রাক্ট এবং `PROGRESS.md` প্ল্যান তৈরি। |
| **Phase 1** | Phase 0 অ্যাপ্রুভালের পর | C# .NET API ও Next.js মনোরেপো ফাউন্ডেশন, ডকার কম্পোজ (Postgres, Redis, MinIO, Meilisearch)। |
| **Phase 2** | Phase 1 শেষ হলে | MechWare সাইবারনেটিক ডিজাইন সিস্টেম, কালার টোকেন, সব UI কম্পোনেন্ট ও শোকেস পেজ। |
| **Phase 3** | Phase 2 শেষ হলে | ইউজার অথেনটিকেশন (JWT, OTP, 2FA), রোল-বেসড পারমিশন (RBAC), অডিট লগ। |
| **Phase 4** | Phase 3 শেষ হলে | প্রোডাক্ট ক্যাটালগ, হাই-কনকারেন্সি স্টক লকিং (ওভারসেলিং বন্ধ), ৪০০+ ইন্ডাস্ট্রিয়াল সিড প্রোডাক্ট। |
| **Phase 5** | Phase 4 শেষ হলে | Meilisearch ইঞ্জিন সেটআপ, টাইপো-টলারেন্ট সার্চ, টেকনিক্যাল ফিল্টার, ইনস্ট্যান্ট অটো-কমপ্লিট। |
| **Phase 6** | Phase 5 শেষ হলে | হোমপেজ, মেগা মেনু, ক্লায়েন্ট লোগো স্ট্রিপ (Square, Singer, RFL), রেস্পন্সিভ মোবাইল ন্যাভবার। |
| **Phase 7** | Phase 6 শেষ হলে | টেকনিক্যাল প্রোডাক্ট পেজ (PDP): স্পেক্স টেবিল, ডেটাশিট PDF ডাউনলোড, CAD লিঙ্ক, RFQ কোটেশন বাটন। |
| **Phase 8** | Phase 7 শেষ হলে | **সবচেয়ে গুরুত্বপূর্ণ**: কার্ট, প্রোমোশন ইঞ্জিন, অ্যাটমিক চেকআউট, পেমেন্ট (Stripe, bKash, SSLCommerz, COD)। |
| **Phase 9** | Phase 8 শেষ হলে | অর্ডার স্টেট মেশিন, কুরিয়ার ট্র্যাকিং, কাস্টমার পোর্টাল, ভেক্টর PDF ইনভয়েস (QuestPDF)। |
| **Phase 10** | Phase 9 শেষ হলে | সেলার সেন্টার, ডাবল-এন্ট্রি ফিনান্সিয়াল লেজার (টাকার নিখুঁত হিসাব, ডেবিট == ক্রেডিট)। |
| **Phase 11** | Phase 10 শেষ হলে | অ্যাডমিন কনসোল (Shopify/Stripe স্টাইলের ড্যাশবোর্ড, KPI কার্ড, ক্যাটালগ ও অর্ডার ম্যানেজার)। |
| **Phase 12** | Phase 11 শেষ হলে | Google Gemini AI ফিচার্স (স্মার্ট সার্চ, রিভিউ সামারি, AI লিস্টিং অ্যাসিস্ট্যান্ট)। |
| **Phase 13** | Phase 12 শেষ হলে | হার্ডেনিং (Lighthouse 90+, সিকিউরিটি অডিট, রেট লিমিটিং, পারফরম্যান্স টিউনিং)। |
| **Phase 14** | Phase 13 শেষ হলে | k6 লোড টেস্টিং (৫,০০০ ইউজার হ্যান্ডেলিং), প্রোডাকশন ডকার বিল্ড, ফাইনাল লঞ্চ! |

---

## ৪. কপি-পেস্ট প্রম্পট চিটশিট (Cheat Sheet)

### প্রম্পট ১: Antigravity-র সেলফ-চেক করার জন্য
যেকোনো ফেজ শেষ হলে Antigravity-তে পেস্ট করবেন:
```text
Run R1 Phase Sign-off Review on Phase [ফেজের নম্বর] against docs/01_PROJECT_BRIEF.md, docs/COMPANY_PROFILE.md, and docs/02_DESIGN_SYSTEM.md.
Open the browser, take screenshots at 360px, 768px, and 1440px in both light and dark mode, run all unit/integration tests, and give me a table of Requirements vs Status with evidence. Fix any partial or missing items before reporting.
```

---

### প্রম্পট ২: Google AI Studio-তে ইন্ডিপেন্ডেন্ট অডিট করানোর জন্য
1. [aistudio.google.com](https://aistudio.google.com/) এ যাবেন।
2. Model: **Gemini 1.5 Pro** সিলেক্ট করবেন (Temperature: 0.2)।
3. **System Instructions** বক্সে দিবেন:
```text
You are a Principal Software Architect, Staff Cybersecurity Auditor, and Lead Product Designer at a top-tier digital engineering agency. Your job is to strictly audit code and architectural documents for an enterprise $100K industrial e-commerce platform (MECHWARE). 
Be extremely critical. Do not flatter. Look for:
1. Missing edge cases, race conditions, or concurrency flaws.
2. Security vulnerabilities (OWASP Top 10, IDOR, token handling, webhook spoofing, client price trust).
3. Deviations from the project brief or design system.
4. Poor UX, amateurish styling, or template-like shortcuts.
Provide an actionable Audit Matrix: Severity (Critical / High / Medium / Low), File/Location, Flaw Explanation, and the Exact Code Fix.
```
4. `+` চেপে `docs/01_PROJECT_BRIEF.md` এবং ওই ফেজের কোড ফাইলগুলো আপলোড করবেন।
5. চ্যাটে প্রম্পট দিবেন:
```text
Audit the attached implementation of Phase [ফেজের নম্বর] against our Project Brief and Design System. Run a full code, architecture, and security review. List every flaw that prevents this from being a true USD 100,000 enterprise-grade build.
```

---

### প্রম্পট ৩: AI Studio-র রিপোর্ট Antigravity-কে দিয়ে ফিক্স করানোর জন্য
AI Studio রিপোর্ট দিলে সেটা কপি করে Antigravity-তে দিবেন:
```text
Google AI Studio External Senior Audit has flagged the following issues on Phase [ফেজের নম্বর]:
[এখানে AI STUDIO-র রিপোর্ট পেস্ট করুন]

Resolve every Critical, High, and Medium issue immediately. Update the code, run regression tests, and show me the before/after proof.
```

---

## ৫. এখনই শুরু করার কমান্ড (Phase 0 Kickoff)

Antigravity চ্যাটে নিচের লাইনটি পেস্ট করে এন্টার দিন:

```text
START PHASE 0: Read docs/01_PROJECT_BRIEF.md, docs/COMPANY_PROFILE.md, and docs/02_DESIGN_SYSTEM.md. Formulate the complete MECHWARE Architecture (C# 13 / .NET 10 + Next.js 15), PostgreSQL ER diagram, API Contract, and PROGRESS.md task list.
```
