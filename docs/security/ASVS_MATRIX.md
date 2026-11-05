# OWASP ASVS 5.0 — Requirement → Test → Evidence matrix

> লক্ষ্য: পুরো প্ল্যাটফর্ম **Level 2**, পেমেন্ট / প্রাইসিং / অ্যাডমিনের জন্য **Level 3** নিয়ন্ত্রণ (`docs/MASTER_PLAN.md` §১, মাপকাঠি ৫)।
> প্রতিটা PR প্রাসঙ্গিক সারি আপডেট করে (`.github/pull_request_template.md`)।
>
> **অবস্থা:** ✅ টেস্ট দিয়ে প্রমাণিত · 🟡 আংশিক / শুধু কোড-রিভিউ · ❌ নেই · 🔴 পরিচিত খোলা সমস্যা
>
> **ASVS ID** (২০২৬-১১-০৫): OWASP ASVS **5.0.0**-এর প্রকাশিত requirement তালিকা থেকে নেওয়া
> (`github.com/OWASP/ASVS`, tag `v5.0.0`, `5.0/docs_en/…_5.0.0_en.flat.json`, ৩৪৫টা requirement)।
> প্রতিটা ID ওই তালিকায় আছে কিনা script দিয়ে মেলানো। ID শুধু সেখানেই বসানো হয়েছে যেখানে নিয়ন্ত্রণ আর requirement স্পষ্ট মেলে;
> যেখানে ASVS-এ সরাসরি কিছু নেই, সেখানে "—" আর কারণ। `(L1)`/`(L2)`/`(L3)` = requirement-এর লেভেল।
> টেস্টের পথ সংক্ষেপে: `UT` = `backend/tests/ShopVerse.UnitTests`, `IT` = `backend/tests/ShopVerse.IntegrationTests`.

## V1 — Encoding and Sanitization
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| Rich text HTML sanitize (XSS) | V1.3.1 (L1) | ✅ | `UT/Catalog/SecurityHardeningTests.cs`, `UT/Properties/InputHardeningFuzzTests.cs` (XSS corpus + random markup) | — |
| URL / slug নিরাপদ (javascript:, data:) | V1.2.2 (L1) | ✅ | `UT/Catalog/SecurityHardeningTests.cs`, `SafeUrl` | — |
| SQL injection — শুধু parameterised query | V1.2.4 (L1) | 🟡 | EF Core + interpolated `ExecuteSql` (parameterised); `IT/Security/ApiSecurityRegressionTests.HostileQueriesAndPaths_NeverCauseA500`; Semgrep ব্লকিং | — |
| লগ injection (correlation id) | V16.4.1 (L2) | 🟡 | `CorrelationIdMiddleware` regex | টেস্ট যোগ করা |

## V2 — Validation and Business Logic
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| দাম শুধু সার্ভার থেকে (price tamper), client-এর অতিরিক্ত field উপেক্ষা | V2.2.2 (L1), V15.3.3 (L2) | ✅ | `IT/Orders/OrdersApiTests.cs` (tamper test), `UT/Orders/OrderPricingTests.cs` | প্রতিটা নতুন endpoint-এ |
| পরিমাণ / লাইন / কুপনের সীমা | V2.3.2 (L2) | ✅ | `IT/Orders/OrderServiceTests.cs`, `OrderPromotionsTests.cs` | সীমাগুলো ডকুমেন্টে (V2.1.3) |
| কুপন ও ক্রেডিটের race; সবকিছু এক transaction-এ | V2.3.3 (L2), V15.4.2 (L3) | ✅ | `IT/Orders/OrderPromotionsTests.cs`, credit race test | — |
| স্টক double-booking (oversell) নয় | V2.3.4 (L2) | ✅ | `IT/Catalog/InventoryConcurrencyTests.cs`, `InventoryChaosTests.cs`, `MultiWarehouseReservationTests.RacingSplitReservations_NeverOversell` | — |
| অর্ডারের ঠিকানা যাচাই | V2.2.1 (L1) | ✅ | `IT/Orders/ShipToTests.cs`, `OrdersApiTests.TheOldFreeTextAddressField_…` (বাগ #৪) | — |
| স্টক hoarding (abuse) | V2.4.1 (L2) | ✅ | `IT/Orders/StockHoardingTests.cs` (বাগ #২) | CAPTCHA (স্প্রিন্ট ১৭–১৮) |
| Approval bypass; বড় অর্ডারে একাধিক জনের অনুমোদন | V2.3.1 (L1), V2.3.5 (L3) | 🔴 | threat model H1: approval অর্ডারের সাথে যুক্ত নয় | স্প্রিন্ট ৭–৯ |
| কোটেশন ও কোম্পানির price list: snapshot দাম, একবার গ্রহণ, 4-eyes | V2.2.2 (L1), V2.3.1 (L1), V2.3.5 (L3) | ❌ | threat model v2 Q1–Q9, R3 (নিয়ম) | স্প্রিন্ট ৭–৯ |
| মূসক-৬.৩ ইনভয়েস: ফাঁকহীন ক্রমিক নম্বর, immutable, credit note | V2.3.3 (L2) | ❌ | threat model v2 I1–I10 (নিয়ম) | স্প্রিন্ট ১০–১২ |

## V3 — Web Frontend Security
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| API-তে HSTS, CSP, nosniff, frame-ancestors, CORP | V3.4.1 (L1), V3.4.3 (L2), V3.4.4 (L2), V3.4.6 (L2), V3.5.8 (L3) | ✅ | `IT/Security/ApiSecurityRegressionTests.EveryResponse_CarriesTheSecurityHeaders`; ZAP (`dast.yml`) | — |
| web / console-এ HSTS, CSP (`object-src 'none'`, `base-uri 'none'`), nosniff, frame-ancestors, COOP | V3.4.1, V3.4.3, V3.4.4, V3.4.6, V3.4.8 (L3) | 🟡 | `frontend/packages/config/security-headers.js`; production build-এ curl দিয়ে যাচাই (বাগ #৭) | header-এর স্বয়ংক্রিয় টেস্ট |
| CSP nonce (no `unsafe-inline`), violation report | V3.4.3 (L3 অংশ), V3.4.7 (L3) | ❌ | — | স্প্রিন্ট ১৭–১৮ |
| CORS শুধু অনুমোদিত origin | V3.4.2 (L1) | ✅ | স্বাধীন টেস্ট: `evil.com` প্রত্যাখ্যাত | localhost origin শুধু Development-এ রাখা |
| Cookie: Secure, SameSite, HttpOnly | V3.3.1 (L1), V3.3.2 (L2), V3.3.4 (L2) | 🟡 | স্বাধীন টেস্ট ২০২৬-১১-০৪ (`secure; samesite=lax; httponly`) | `__Host-` / `__Secure-` prefix (V3.3.1, V3.3.3) — স্প্রিন্ট ১৭–১৮ |
| HSTS preload তালিকা | V3.7.4 (L3) | ❌ | — | ডোমেইন চালু হলে |

## V4 — API and Web Service
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| প্রতিটা endpoint সুরক্ষিত বা পর্যালোচিত পাবলিক তালিকায় | V8.2.1 (L1), V8.3.1 (L1) | ✅ | `IT/Security/ApiSecurityRegressionTests.EveryEndpoint_IsProtectedOrOnTheReviewedPublicAllowList` | — |
| বিকৃত / শত্রুভাবাপন্ন ইনপুটে 500 নয়, fail closed | V16.5.3 (L2) | 🟡 | `HostileInput_NeverCausesA500_OrLeaksInternals`; অর্ডারের ঠিকানা আর client থেকে আসে না (বাগ #৪) | লগইন-করা endpoint-গুলোতে fuzz যোগ করা |
| বড় body প্রত্যাখ্যান | — (ASVS 5.0-এ request body আকারের সরাসরি requirement নেই; সম্পর্কিত: V15.2.2) | ✅ | webhook 64 KB সীমা (R3 রিভিউ); Kestrel সীমা | — |
| Idempotency (একই key-তে একটাই অর্ডার) | — (সরাসরি requirement নেই; সম্পর্কিত: V2.3.3) | ✅ | `IT/Orders/OrderServiceTests.cs` (একই key-তে ৮ সমান্তরাল retry → ১ অর্ডার) | — |
| Payment webhook: প্রতি বার্তায় স্বাক্ষর, replay নয় | V4.1.5 (L3) | ✅ | `UT/Payments/WebhookVerifierTests.cs`, `IT/Orders/PaymentServiceTests.cs` | — |
| Payment event সাময়িক ত্রুটিতে হারায় না; এক অর্ডারে দ্বিতীয় পেমেন্ট refund-এ চিহ্নিত | V2.3.3 (L2), V15.4.2 (L3) | ✅ | `IT/Orders/PaymentServiceTests.cs`: `ATransientProviderFailure_…`, `AnAttemptThatDiedMidway_…`, `ASecondPaidSessionForAConfirmedOrder_…`, `OneOrder_CannotCollectPaymentAttemptsWithoutBound` (mutation-checked) | refund-এর কাজের তালিকা (P3) |
| Refund-এ 4-eyes, অঙ্ক payment-এর বেশি নয়, দুবার টাকা পাঠানো নয় | V2.3.5 (L3), V2.3.3 (L2) | ✅ | `IT/Orders/RefundServiceTests.cs`, `IT/Api/RefundEndpointsTests.cs`, `UT/Payments/RefundRulesTests.cs` (mutation-checked) | step-up auth, কনসোল UI |
| কুপন কোড গণনা ঠেকানো | V2.4.1 (L2) | ✅ | `IT/Orders/OrderPromotionsTests.cs`: `AnExpiredNotYetStartedOrUsedUpCode_…`, `MixingARealCodeWithAGuess_…`, `TooManyRefusedCodes_…`, `ParallelRefusals_AreAllCounted` (mutation-checked) | — |
| Proxy-র পেছনে আসল client IP (rate limit) | V15.3.4 (L2), V4.1.3 (L2) | ✅ | `UT/Configuration/ForwardedHeadersSettingsTests.cs` (বাগ #৬) | ডেপ্লয়ে মান সেট করা |

## V5 — File Handling
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| আপলোড: আকার, টাইপ/extension, সংরক্ষিত ফাইল চালানো যায় না | V5.2.1 (L1), V5.2.2 (L1), V5.3.1 (L1) | ❌ | — | স্প্রিন্ট ৩–৪ (PIM) |
| ফাইল path তৈরি বিশ্বস্ত ডেটা থেকে, root-এর বাইরে লেখা যায় না | V5.3.2 (L1) | ✅ | `UT/Catalog/SeedMediaPathTests.cs` (seed media; Semgrep `unsafe-path-combine`) | আপলোড এলে একই নিয়ম |

## V6 — Authentication
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| পাসওয়ার্ড hashing (PBKDF2-SHA512, 210k) ও ন্যূনতম দৈর্ঘ্য | V11.4.2 (L2), V6.2.1 (L1) | ✅ | `UT/Identity/IdentityPhase3UnitTests.cs` | breached/সাধারণ পাসওয়ার্ড চেক (V6.2.4, V6.2.12) |
| Brute-force throttling ও lockout | V6.3.1 (L1) | ✅ | `IT/Security/ApiSecurityRegressionTests.Production_HidesSwagger_AndThrottlesLoginBruteForce`, `CredentialEndpoints_UseTheStrictRateLimitPolicy` | — |
| TOTP একবারই ব্যবহার (replay guard) | V6.5.1 (L2) | ✅ | `IT/Identity/IdentityServiceTests.cs`, console E2E | — |
| স্টাফের MFA | V6.3.3 (L2) | 🟡 | admin / finance-এ TOTP বাধ্যতামূলক | অন্য স্টাফ (M1); passkey (L3) |
| Production-এ default/demo অ্যাকাউন্ট নেই | V6.3.2 (L1) | ✅ | `Production_HasNoDemoAccounts_…` (খালি DB) + `IT/Security/DemoAccountGuardTests.cs` (Development-এ চালানো DB-তে Production/Staging চালু হয় না) | — |
| অ্যাকাউন্ট আছে কিনা ফাঁস না করা (registration, forgot password) | V6.3.8 (L3) | ✅ | `IdentityServiceTests.RegisteringAnEmailOrPhoneThatHasAnAccount_…`, `AnInvalidRegistration_IsRefusedTheSameWay_…` (বাগ #৫) | login-এর response time মেলানো যাচাই |
| Bootstrap admin শুধু পাসওয়ার্ড + TOTP | V6.3.3 (L2) | ✅ | `Production_BootstrapAdmin_CanSignInOnlyWithPasswordAndTotp`, `UT/Identity/BootstrapAdminTests.cs` | — |

## V7 — Session Management
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| Refresh token: CSPRNG reference token, rotation, reuse-এ পুরো family বাতিল | V7.2.3 (L1), V7.4.1 (L1) | ✅ | `IT/Identity/IdentityServiceTests.cs` (row lock mutation-checked) | — |
| পাসওয়ার্ড বদলালে অন্য সব session বাতিল | V7.4.3 (L2) | ✅ | `UT/Identity/IdentityPhase3UnitTests.cs` | — |
| Session-এর inactivity / সর্বোচ্চ আয়ু | V7.3.1 (L2), V7.3.2 (L2) | 🟡 | access 15 মিনিট, refresh 7 দিন (`appsettings.json`) | ডকুমেন্টে যুক্তি (V7.1.1) |

## V8 — Authorization
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| Permission-ভিত্তিক (role নয়), API-তে চেক | V8.2.1 (L1), V8.3.1 (L1) | ✅ | `PermissionAuthorizationHandler`, `IT/Api/AuthAndRbacIntegrationTests.cs` | — |
| কোম্পানি IDOR (404) | V8.2.2 (L1) | ✅ | `IT/Companies/CompanyServiceTests.cs`, `ApprovalServiceTests.cs` (প্রতিটা অপারেশন) | global query filter |
| অর্ডার / পেমেন্ট / ঠিকানা / site IDOR | V8.2.2 (L1) | ✅ | `IT/Orders/OrdersApiTests.cs`, `ShipToTests.cs`; স্বাধীন টেস্ট | — |
| Seller isolation | V8.2.2 (L1) | ✅ | `SellerResourceIsolationHandler` + IT | — |
| Step-up auth (refund, price list, role) | — (ASVS 5.0-এ শুধু account-এর ক্ষেত্রে: V7.5.1) | ❌ | — | স্প্রিন্ট ১৫–১৬ |
| অ্যাডমিন ইন্টারফেসে একাধিক স্তর | V8.4.2 (L3) | ❌ | — | console Zero-Trust (স্প্রিন্ট ১৫–১৮) |

## V9 — Self-contained Tokens (JWT)
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| MAC যাচাই, `alg` allowlist, `none`/confusion প্রত্যাখ্যাত | V9.1.1 (L1), V9.1.2 (L1) | ✅ | `UT/Identity/JwtTamperingTests.cs` | — |
| `exp` / `nbf`, `aud` যাচাই | V9.2.1 (L1), V9.2.3 (L2) | ✅ | `JwtTamperingTests.MissingOrWrongIssuerOrAudience_IsRejected`, `ExpiredToken_…`, `NotYetValidToken_…` | — |
| আকারের সীমা, garbage-এ throw নয় | — (সরাসরি requirement নেই) | ✅ | `OversizedToken_IsRejectedWithoutParsing`, `RandomGarbage_NeverThrowsAndIsNeverAccepted` | — |
| প্রকাশিত dev secret Production-এ অচল | — (সম্পর্কিত: V13.3.1 secret management) | ✅ | `Production_RefusesToStart_WithThePublishedDevelopmentJwtSecret` | Vault / KMS (V13.3.1) |

## V10 — OAuth and OIDC
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| Google ID token যাচাই | V9.1.1 (L1), V9.2.3 (L2), V10.5.2 (L2) | 🟡 | `IdentityService.GoogleLogin` (`iss`, `exp`, `email_verified`, `sub`) | signature (JWKS) টেস্টসহ, `aud` = client id |

## V11 — Cryptography
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| Constant-time তুলনা (webhook, OTP) | V11.2.4 (L3) | ✅ | `UT/Payments/WebhookVerifierTests.cs` | — |
| সংবেদনশীল ডেটা (BIN/TIN, NID) এনক্রিপশন, key KMS-এ | V14.2.4 (L2), V13.3.1 (L2) | ❌ | threat model H4 | স্প্রিন্ট ১৭–১৮ |

## V12 — Secure Communication
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| বাইরের সব যোগাযোগ TLS, TLS 1.2+ | V12.2.1 (L1), V12.1.1 (L1) | 🟡 | HSTS header আছে | edge (Cloudflare) কনফিগ, স্প্রিন্ট ১৭–১৮ |

## V13 — Configuration
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| গোপন config ছাড়া Production চালু হয় না (fail closed) | — (সম্পর্কিত: V13.3.1, V16.5.3) | ✅ | `Production_RefusesToStart_WithoutAJwtSecret`, `UT/Configuration/StartupOptionsValidationTests.cs` | — |
| Swagger / API documentation Production-এ বন্ধ | V13.4.5 (L2) | ✅ | `Production_HidesSwagger_AndThrottlesLoginBruteForce` | — |
| Production-এ শুধু দরকারি কার্যকারিতা (mock গেটওয়ে নেই) | V15.2.3 (L2) | ✅ | `docs/reviews/R3_ORDERS_AND_PAYMENTS.md`; স্বাধীন টেস্ট | — |
| Dependency: patch SLA ডকুমেন্টে, পুরনো ঝুঁকিপূর্ণ সংস্করণ নেই | V15.1.1 (L1), V15.2.1 (L1) | ✅ | `MASTER_PLAN.md` §৬ (SLA); `.github/workflows/security.yml` (NuGet + npm audit, Trivy) | SBOM (V15.1.2) |
| Secret scanning, SAST | — (প্রক্রিয়া-নিয়ন্ত্রণ, সরাসরি requirement নেই) | ✅ | gitleaks, CodeQL, Semgrep, Trivy — সব ব্লকিং; GitHub secret scanning + push protection চালু | — |

## V14 — Data Protection
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| ব্যক্তিগত ডেটা export ও মুছে ফেলা | — (PDPO 2025-এর দাবি; ASVS-এ সম্পর্কিত: V14.2.7 retention) | 🟡 | `GET /users/me/export`, `DELETE /users/me` | PDPO consent রেকর্ড, ঠিকানা anonymise (স্প্রিন্ট ১৭–১৮) |
| সংবেদনশীল ডেটার শ্রেণিবিভাগ ও সুরক্ষা-নিয়ম ডকুমেন্টে | V14.1.1 (L2), V14.1.2 (L2) | ❌ | — | data inventory (স্প্রিন্ট ১৭–১৮) |

## V15 — Secure Coding and Architecture
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| লেয়ারিং নিয়ম (Domain-এর কোনো dependency নেই, controller-এ Infrastructure নয়) | — (স্থাপত্য-চর্চা, সরাসরি requirement নেই) | ✅ | `UT/Architecture/LayeringTests.cs` | মডিউলের সীমানা |
| Concurrency: check আর action একসাথে (TOCTOU নয়), lock-এর নির্দিষ্ট ক্রম | V15.4.2 (L3), V15.4.3 (L3) | ✅ | `IT/Catalog/InventoryConcurrencyTests.cs`, `InventoryChaosTests.cs`, `StockHoardingTests.ParallelOrdersWithDifferentKeys_…`, অর্ডার/পেমেন্ট race টেস্ট | — |

## V16 — Security Logging and Error Handling
| নিয়ন্ত্রণ | ASVS ID | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|---|
| অপ্রত্যাশিত error-এ সাধারণ বার্তা, ভেতরের তথ্য নেই | V16.5.1 (L2) | ✅ | `HostileInput_NeverCausesA500_OrLeaksInternals`; স্বাধীন টেস্ট (Production 500 শুধু trace id দেয়) | — |
| নিরাপত্তা ঘটনার audit (login, lockout, credit, approval, registration) | V16.3.1 (L2), V16.3.3 (L2) | 🟡 | explicit audit events | log সুরক্ষা / tamper-evident (V16.4.2), আলাদা সিস্টেমে পাঠানো (V16.4.3) |
| Audit log-এ secret redaction | V16.2.5 (L2) | ✅ | `AuditLogSaveChangesInterceptor` | — |
