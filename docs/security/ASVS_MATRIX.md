# OWASP ASVS 5.0 — Requirement → Test → Evidence matrix

> লক্ষ্য: পুরো প্ল্যাটফর্ম **Level 2**, পেমেন্ট / প্রাইসিং / অ্যাডমিনের জন্য **Level 3** নিয়ন্ত্রণ (`docs/MASTER_PLAN.md` §১, মাপকাঠি ৫)।
> প্রতিটা PR প্রাসঙ্গিক সারি আপডেট করে (`.github/pull_request_template.md`)।
>
> **অবস্থা:** ✅ টেস্ট দিয়ে প্রমাণিত · 🟡 আংশিক / শুধু কোড-রিভিউ · ❌ নেই · 🔴 পরিচিত খোলা সমস্যা
>
> এটা কঙ্কাল (২০২৬-১১-০৪)। সারিগুলো অধ্যায় ও নিয়ন্ত্রণ অনুযায়ী সাজানো; প্রতিটা সারিতে অফিসিয়াল ASVS 5.0
> requirement ID (`owasp.org/asvs`) বসানো স্প্রিন্ট ০-এর কাজ — অনুমানে ID লেখা হয়নি।
> টেস্টের পথ সংক্ষেপে: `UT` = `backend/tests/ShopVerse.UnitTests`, `IT` = `backend/tests/ShopVerse.IntegrationTests`.

## V1 — Encoding and Sanitization
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Rich text HTML sanitize (XSS) | ✅ | `UT/Catalog/SecurityHardeningTests.cs`, `UT/Properties/InputHardeningFuzzTests.cs` (XSS corpus + random markup) | — |
| URL / slug নিরাপদ (javascript:, data:) | ✅ | `UT/Catalog/SecurityHardeningTests.cs`, `SafeUrl` | — |
| SQL injection — শুধু parameterised query | 🟡 | EF Core + interpolated `ExecuteSql` (parameterised); `IT/Security/ApiSecurityRegressionTests.HostileQueriesAndPaths_NeverCauseA500` | Semgrep নিয়ম CI-তে (চালু) |
| লগ injection (correlation id) | ✅ | `CorrelationIdMiddleware` regex | টেস্ট যোগ করা |

## V2 — Validation and Business Logic
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| দাম শুধু সার্ভার থেকে (price tamper) | ✅ | `IT/Orders/OrdersApiTests.cs` (tamper test), `UT/Orders/OrderPricingTests.cs` | প্রতিটা নতুন endpoint-এ |
| পরিমাণ / লাইন / কুপনের সীমা | ✅ | `IT/Orders/OrderServiceTests.cs` | — |
| কুপন ও ক্রেডিটের race | ✅ | `IT/Orders/OrderPromotionsTests.cs`, credit race test | — |
| অর্ডারের ঠিকানা যাচাই | 🔴 | বাগ #৪: `shipToJson`-এ ভুল JSON → 500, ২ MB গ্রহণ, ঠিকানা ছাড়া অর্ডার | স্প্রিন্ট ০ |
| স্টক hoarding (abuse) | 🔴 | বাগ #২: যাচাই না করা অ্যাকাউন্ট, unpaid অর্ডারের সীমা নেই | স্প্রিন্ট ০ |
| Approval bypass | 🔴 | threat model H1: approval অর্ডারের সাথে যুক্ত নয় | স্প্রিন্ট ৭–৯ |

## V3 — Web Frontend Security
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| API-তে CSP, HSTS, X-Frame-Options, nosniff | ✅ | `SecurityHeadersMiddleware`; স্বাধীন টেস্ট ২০২৬-১১-০৪ | টেস্ট যোগ করা |
| web / console-এ security header | 🔴 | বাগ #৭ (threat model M5) | স্প্রিন্ট ০ |
| CSP nonce (no `unsafe-inline`) | ❌ | — | স্প্রিন্ট ১৭–১৮ |
| CORS শুধু অনুমোদিত origin | ✅ | স্বাধীন টেস্ট: `evil.com` প্রত্যাখ্যাত | localhost origin শুধু Development-এ রাখা |

## V4 — API and Web Service
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| প্রতিটা endpoint সুরক্ষিত বা পর্যালোচিত পাবলিক তালিকায় | ✅ | `IT/Security/ApiSecurityRegressionTests.EveryEndpoint_IsProtectedOrOnTheReviewedPublicAllowList` | — |
| বিকৃত / শত্রুভাবাপন্ন ইনপুটে 500 নয় | 🟡 | `HostileInput_NeverCausesA500_OrLeaksInternals` | বাগ #৪ এই টেস্টে ধরা পড়েনি — অর্ডার body যোগ করা |
| বড় body প্রত্যাখ্যান | ✅ | webhook 64 KB সীমা (R3 রিভিউ); Kestrel সীমা | — |
| Idempotency | ✅ | `IT/Orders/OrderServiceTests.cs` (একই key-তে ৮ সমান্তরাল retry → ১ অর্ডার) | — |

## V5 — File Handling
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| টাইপ allow-list, magic byte, আকার, AV scan, signed URL | ❌ | — | স্প্রিন্ট ৩–৪ |

## V6 — Authentication
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| পাসওয়ার্ড hashing ও policy | ✅ | `UT/Identity/IdentityPhase3UnitTests.cs` | — |
| Brute-force throttling ও lockout | ✅ | `IT/Security/ApiSecurityRegressionTests.Production_HidesSwagger_AndThrottlesLoginBruteForce`, `CredentialEndpoints_UseTheStrictRateLimitPolicy` | proxy-এর পেছনে বাগ #৬ |
| স্টাফের জন্য TOTP + replay guard | ✅ | `IT/Identity/IdentityServiceTests.cs`, console E2E | admin/finance ছাড়া অন্য স্টাফ (M1); passkey |
| Production-এ demo অ্যাকাউন্ট নেই | 🔴 | `Production_HasNoDemoAccounts_EveryPublishedCredentialIsRefused` পাস করে **খালি DB-তে**; বাগ #১: Development-এ তৈরি অ্যাকাউন্ট DB-তে থাকলে Production পুরো admin দেয় | স্প্রিন্ট ০ |
| অ্যাকাউন্ট আছে কিনা ফাঁস না করা | 🔴 | forgot-password ঠিক; বাগ #৫: রেজিস্ট্রেশন ফাঁস করে | স্প্রিন্ট ০ |
| Bootstrap admin শুধু পাসওয়ার্ড + TOTP | ✅ | `Production_BootstrapAdmin_CanSignInOnlyWithPasswordAndTotp`, `UT/Identity/BootstrapAdminTests.cs` | — |

## V7 — Session Management
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Refresh token rotation + reuse detection | ✅ | `IT/Identity/IdentityServiceTests.cs` (row lock mutation-checked) | — |
| Cookie: HttpOnly, Secure, SameSite, path-scoped | ✅ | স্বাধীন টেস্ট ২০২৬-১১-০৪ | `__Host-` prefix / BFF (স্প্রিন্ট ১৭–১৮) |
| পাসওয়ার্ড বদলালে সব session বাতিল | ✅ | `UT/Identity/IdentityPhase3UnitTests.cs` | — |

## V8 — Authorization
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Permission-ভিত্তিক (role নয়) | ✅ | `PermissionAuthorizationHandler`, `IT/Api/AuthAndRbacIntegrationTests.cs` | — |
| কোম্পানি IDOR (404) | ✅ | `IT/Companies/CompanyServiceTests.cs`, `ApprovalServiceTests.cs` (প্রতিটা অপারেশন) | global query filter |
| অর্ডার / পেমেন্ট IDOR | ✅ | `IT/Orders/OrdersApiTests.cs`; স্বাধীন টেস্ট | — |
| Seller isolation | ✅ | `SellerResourceIsolationHandler` + IT | — |
| Step-up auth (refund, price list, role) | ❌ | — | স্প্রিন্ট ১৫–১৬ |

## V9 — Self-contained Tokens (JWT)
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| `alg` নির্দিষ্ট, `none` প্রত্যাখ্যাত, algorithm confusion | ✅ | `UT/Identity/JwtTamperingTests.cs` | — |
| `iss`, `aud`, `exp`, `nbf` যাচাই | ✅ | `JwtTamperingTests.MissingOrWrongIssuerOrAudience_IsRejected`, `ExpiredToken_…`, `NotYetValidToken_…` | — |
| আকারের সীমা, garbage-এ throw নয় | ✅ | `OversizedToken_IsRejectedWithoutParsing`, `RandomGarbage_NeverThrowsAndIsNeverAccepted` | — |
| প্রকাশিত dev secret Production-এ অচল | ✅ | `Production_RefusesToStart_WithThePublishedDevelopmentJwtSecret` | — |

## V10 — OAuth and OIDC
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Google ID token claim যাচাই (`iss`, `exp`, `email_verified`) | 🟡 | `IdentityService.GoogleLogin` (AUD-P3-02) | signature যাচাই (JWKS) টেস্টসহ, `aud` = client id |

## V11 — Cryptography
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Constant-time তুলনা (webhook, OTP) | ✅ | `UT/Payments/WebhookVerifierTests.cs` | — |
| PII এনক্রিপশন (BIN/TIN, NID) + KMS | ❌ | threat model H4 | স্প্রিন্ট ১৭–১৮ |

## V12 — Secure Communication
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| শুধু HTTPS, HSTS preload | 🟡 | API header আছে | edge (Cloudflare) কনফিগ, স্প্রিন্ট ১৭–১৮ |

## V13 — Configuration
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| গোপন config ছাড়া Production চালু হয় না (fail closed) | ✅ | `Production_RefusesToStart_WithoutAJwtSecret`, `UT/Configuration/StartupOptionsValidationTests.cs` | — |
| Swagger Production-এ বন্ধ | ✅ | `Production_HidesSwagger_AndThrottlesLoginBruteForce` | — |
| Mock পেমেন্ট গেটওয়ে শুধু Development-এ | ✅ | `docs/reviews/R3_ORDERS_AND_PAYMENTS.md`; স্বাধীন টেস্ট | — |
| Dependency vulnerability gate | ✅ | `.github/workflows/security.yml` (NuGet + npm audit) | — |
| Secret scanning, SAST, config scan | 🟡 | CodeQL; gitleaks, Semgrep, Trivy যোগ হলো (২০২৬-১১-০৪) | GitHub secret scanning চালু (মালিক) |
| Reverse proxy-র known networks | 🔴 | বাগ #৬ | স্প্রিন্ট ০ |

## V14 — Data Protection
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| ব্যক্তিগত ডেটা export ও মুছে ফেলা | 🟡 | `GET /users/me/export`, `DELETE /users/me` | PDPO consent রেকর্ড, ঠিকানা anonymise (স্প্রিন্ট ১৭–১৮) |
| Audit log-এ secret redaction | ✅ | `AuditLogSaveChangesInterceptor` | tamper-evident hash chain |

## V15 — Secure Coding and Architecture
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| লেয়ারিং নিয়ম (Domain-এর কোনো dependency নেই, controller-এ Infrastructure নয়) | ✅ | `UT/Architecture/LayeringTests.cs` | মডিউলের সীমানা (স্প্রিন্ট ০+) |
| Concurrency: স্টক, ক্রেডিট, কুপন, approval, পেমেন্ট | ✅ | `IT/Catalog/InventoryConcurrencyTests.cs`, `InventoryChaosTests.cs`, অর্ডার/পেমেন্ট race টেস্ট | — |

## V16 — Security Logging and Error Handling
| নিয়ন্ত্রণ | অবস্থা | টেস্ট / প্রমাণ | বাকি |
|---|---|---|---|
| Production error-এ ভেতরের তথ্য নেই | ✅ | `HostileInput_NeverCausesA500_OrLeaksInternals`; স্বাধীন টেস্ট (Production 500 শুধু trace id দেয়) | — |
| নিরাপত্তা ঘটনার audit (login, lockout, credit, approval) | 🟡 | explicit audit events | tamper-evident, অ্যালার্ট (স্প্রিন্ট ১৭–১৮) |
| Webhook replay / স্বাক্ষর | ✅ | `UT/Payments/WebhookVerifierTests.cs`, `IT/Orders/PaymentServiceTests.cs` | — |
