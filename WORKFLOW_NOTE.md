# 🚀 MECHWARE — সহজ কাজের নিয়ম (DAILY WORKFLOW NOTE)

> **মনে রাখার সহজ নিয়ম:** এই ফাইলটি দেখলেই বুঝতে পারবেন প্রতিদিন কীভাবে কাজ করবেন। পুরো প্রজেক্ট শেষ না হওয়া পর্যন্ত প্রতিটি ফেজ এই একই ৪-ধাপের নিয়মে চলবে।

---

## 📌 আমাদের ৩টি মূল লিংক (Quick Links)

1. **লোকাল প্রজেক্ট ফোল্ডার**: `F:\CHAT GPT\WEBSIDE`
2. **গিটহাব রিপোজিটরি**: [https://github.com/parvezmosharof2-commits/mechware-](https://github.com/parvezmosharof2-commits/mechware-)
3. **গুগল এআই স্টুডিও (রিভিউ করার জন্য)**: [https://aistudio.google.com/](https://aistudio.google.com/)

---

## 🔄 প্রতিটি ফেজের ৪-ধাপের সাইকেল (The 4-Step Loop)

```
[ধাপ ১: Antigravity-তে ফেজ শুরু করা]
                 ↓
[ধাপ ২: Antigravity কোড লিখে GitHub-এ Push করবে]
                 ↓
[ধাপ ৩: Google AI Studio-তে কোড দিয়ে অডিট করানো]
                 ↓
[ধাপ ৪: AI Studio-র রিপোর্ট Antigravity-কে দিয়ে Fix করানো]
```

---

### বিস্তারিত নিয়ম:

### ধাপ ১: Antigravity-তে কমান্ড দেওয়া
আপনি শুধু Antigravity-র চ্যাটে বলবেন:
> *"START PHASE [নম্বর]"* (যেমন: `START PHASE 0` বা `START PHASE 1`)

### ধাপ ২: Antigravity কাজ করবে ও গিটহাবে পাঠাবে
- Antigravity আপনার কম্পিউটারের `F:\CHAT GPT\WEBSIDE` ফোল্ডারে সব ফাইল ও কোড তৈরি করবে।
- নিজে টেস্ট রান করে দেখবে কোনো ভুল আছে কিনা।
- কাজ শেষ হলে Antigravity স্বয়ংক্রিয়ভাবে গিটহাবে (`git push`) কোড পাঠিয়ে দিবে।

### ধাপ ৩: Google AI Studio-তে এক্সটার্নাল অডিট করানো
1. ব্রাউজারে [aistudio.google.com](https://aistudio.google.com/) এ যাবেন (Model: Gemini 1.5 Pro)।
2. গিটহাব থেকে নতুন কোড বা জিপ ফাইল ড্রপ করবেন।
3. প্রম্পট দিবেন:
   > *"Perform a strict Principal Engineer & Security Audit on this Phase [নম্বর] code against docs/01_PROJECT_BRIEF.md. List every flaw and exact fixes."*
4. AI Studio আপনাকে একটি নিরপেক্ষ অডিট রিপোর্ট দিবে।

### ধাপ ৪: Antigravity দিয়ে ফিক্স করানো
AI Studio যে রিপোর্ট বা পরামর্শ দিবে, সেটা কপি করে এনে Antigravity-তে পেস্ট করে বলবেন:
> *"AI Studio-র অডিটে এই সমস্যাগুলো এসেছে, এগুলো কোডে ফিক্স করে আবার টেস্ট পাস করাও এবং গিটহাবে পুশ দাও।"*
- Antigravity কোড ঠিক করে আবার গিটহাবে পুশ দিয়ে দিবে। **ব্যস, ওই ফেজের কাজ ১০০% পারফেক্ট!**

---

## 🏁 এখনই প্রথম কদম (Phase 0 শুরু করার নিয়ম)

Phase 0-তে আমরা কোনো কোড না লিখে আগে পুরো প্রজেক্টের আর্কিটেকচার ব্লুপ্রিন্ট, ডাটাবেস ER ডায়াগ্রাম এবং API কন্ট্রাক্ট তৈরি করব।

শুরু করার জন্য চ্যাটে শুধু নিচের লাইনটি লিখুন:
```text
START PHASE 0
```
