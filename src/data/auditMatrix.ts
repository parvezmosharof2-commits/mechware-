export interface AuditFinding {
  id: string;
  severity: 'Critical' | 'High' | 'Medium' | 'Low';
  phase: 'Phase 0 (Docs/Schema)' | 'Phase 1 (Backend/Infra)' | 'Phase 2 (Design System/UI)';
  category:
    | 'Tailwind & Design Tokens'
    | 'Security (OWASP ASVS L2)'
    | 'API Client & Reliability'
    | 'CI/CD & Testcontainers'
    | 'Accessibility & UX'
    | 'Schema & Spec Consistency';
  fileLocation: string;
  title: string;
  flawExplanation: string;
  impact: string;
  exactCodeFix: string;
  language: 'sql' | 'csharp' | 'json' | 'typescript';
}

export const PHASE_0_AUDIT_FINDINGS: AuditFinding[] = [
  {
    id: 'MW-P2-NEW-01',
    severity: 'Critical',
    phase: 'Phase 2 (Design System/UI)',
    category: 'Tailwind & Design Tokens',
    fileLocation:
      'frontend/packages/config/tailwind.preset.js (Lines 40–45) & ProductCard.tsx, Stepper.tsx, Toast.tsx, AlertBanner.tsx, PhoneInput.tsx, FileUploader.tsx',
    title: 'Missing `status.*` Color Namespace in Tailwind Preset Breaks 11 Core UI Components & Apps',
    flawExplanation:
      'Across 11 files (`ProductCard.tsx:115,281`, `Stepper.tsx:73,110`, `Toast.tsx:30-39`, `AlertBanner.tsx:26-33`, `PhoneInput.tsx:78,117`, `FileUploader.tsx:142`, `Badge.tsx:62`, `Avatar.tsx:15`, `Progress.tsx:15-16`, `apps/web/page.tsx:10`, and `apps/console/page.tsx:15,24,34,38`), components use `bg-status-online`, `text-status-online`, `border-status-online`, `ring-status-online`, `bg-status-pending`, `text-status-pending`, `text-status-fault`, and `border-status-fault`. However, `tailwind.preset.js` only defines flat keys (`online`, `warning`, `pending`, `fault`) and omits the nested `status: { ... }` object! Tailwind generates ZERO CSS for every `*-status-*` utility.',
    impact:
      'Clicking "Add to Cart" on ProductCard (`bg-status-online text-bg`) or completing a checkout step in Stepper (`bg-status-online text-bg`) renders dark `#060B18` text on a transparent background over a `#0D1635` dark card — making the primary CTA and active status indicators invisible!',
    language: 'typescript',
    exactCodeFix: `// In frontend/packages/config/tailwind.preset.js (inside theme.extend.colors):
        online: 'var(--status-online)',
        success: 'var(--status-online)',
        warning: 'var(--status-pending)',
        pending: 'var(--status-pending)',
        fault: 'var(--status-fault)',
        deal: 'var(--deal)',
        status: {
          online: 'var(--status-online)',
          success: 'var(--status-online)',
          pending: 'var(--status-pending)',
          warning: 'var(--status-pending)',
          fault: 'var(--status-fault)',
        },`
  },
  {
    id: 'MW-P1-NEW-02',
    severity: 'High',
    phase: 'Phase 1 (Backend/Infra)',
    category: 'Security (OWASP ASVS L2)',
    fileLocation:
      'backend/src/ShopVerse.Api/Middleware/SecurityHeadersMiddleware.cs (Line 21) & backend/src/ShopVerse.Api/ShopVerse.Api.csproj',
    title: 'Strict `default-src \'none\'` CSP Blocks Swagger UI (`/swagger`) & XML Comments Are Disabled',
    flawExplanation:
      '1) `SecurityHeadersMiddleware` runs before `app.UseSwaggerUI()` (`Program.cs:151` vs `164`) and sets `Content-Security-Policy: default-src \'none\'` on every response. Opening `/swagger/index.html` in a browser fails completely because Swagger UI inline/bundle scripts, styles, and OpenAPI JSON fetches are blocked by CSP. 2) Phase 1 Requirement 2 mandates "OpenAPI / Swagger documentation with XML comments and JWT bearer auth at /swagger", but `ShopVerse.Api.csproj` does not enable `<GenerateDocumentationFile>true</GenerateDocumentationFile>` and `AddSwaggerGen` does not call `IncludeXmlComments`.',
    impact:
      'Swagger UI (`/swagger`) renders a blank broken page in the browser, violating Phase 1 Deliverable #2.',
    language: 'csharp',
    exactCodeFix: `// 1. In backend/src/ShopVerse.Api/Middleware/SecurityHeadersMiddleware.cs:
var isSwagger = context.Request.Path.StartsWithSegments("/swagger") ||
                context.Request.Path.StartsWithSegments("/openapi");
headers.TryAdd("Content-Security-Policy", isSwagger
    ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'"
    : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'");

// 2. In backend/src/ShopVerse.Api/ShopVerse.Api.csproj (<PropertyGroup>):
<GenerateDocumentationFile>true</GenerateDocumentationFile>
<NoWarn>$(NoWarn);1591</NoWarn>

// 3. In backend/src/ShopVerse.Api/Program.cs (inside AddSwaggerGen):
var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFilename);
if (System.IO.File.Exists(xmlPath)) c.IncludeXmlComments(xmlPath);`
  },
  {
    id: 'MW-P1-NEW-03',
    severity: 'High',
    phase: 'Phase 1 (Backend/Infra)',
    category: 'Security (OWASP ASVS L2)',
    fileLocation: 'backend/src/ShopVerse.Api/Program.cs (Lines 56–62 & 96–105)',
    title: 'Unrestricted `ForwardedHeadersOptions` Allows Trivial Rate-Limit Bypass via `X-Forwarded-For` Spoofing',
    flawExplanation:
      'In `Program.cs`, `options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();` trusts `X-Forwarded-For` from ANY peer without setting `ForwardLimit = 1` or restricting trusted proxy networks in production. Because `GlobalLimiter` partitions by `httpContext.Connection.RemoteIpAddress`, an attacker can bypass the 100 req/min rate limiter infinitely by sending a random `X-Forwarded-For: <random-ip>` header on every request.',
    impact:
      'Complete bypass of global IP rate limiting (`100 req/min`) and brute-force protection on authentication/OTP endpoints.',
    language: 'csharp',
    exactCodeFix: `builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    // Only clear KnownProxies in local Development/Testing; in Production bind to configured ReverseProxy CIDRs
    if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});`
  },
  {
    id: 'MW-P1-NEW-04',
    severity: 'High',
    phase: 'Phase 1 (Backend/Infra)',
    category: 'API Client & Reliability',
    fileLocation: 'frontend/packages/api-client/src/index.ts (Lines 55–70)',
    title: '`MechWareApiClient` Double-Reads Consumed Fetch Body Stream on Non-JSON Error Responses',
    flawExplanation:
      'In `MechWareApiClient.fetch()`, when `!response.ok`, the `try` block calls `await response.json()`. If the upstream server or reverse proxy returns a non-JSON error body (e.g., 502/503 HTML from Cloudflare/Nginx or plain-text 429), `response.json()` consumes the `ReadableStream` and throws a `SyntaxError`. Execution enters the `catch` block, which calls `await response.text()` on the ALREADY consumed stream — throwing `TypeError: Body is unusable: Body has already been read` and swallowing the actual HTTP status code!',
    impact:
      'Unhandled `TypeError` crash in Next.js SSR/CSR whenever the API or gateway returns a non-JSON error response.',
    language: 'typescript',
    exactCodeFix: `    if (!response.ok) {
      const rawBody = await response.text();
      let problem: ProblemDetails;
      try {
        problem = JSON.parse(rawBody) as ProblemDetails;
      } catch {
        problem = {
          title: response.statusText || 'HTTP Error',
          status: response.status,
          detail: rawBody || \`Request failed with status \${response.status}\`,
        };
      }
      throw new ApiError(problem);
    }`
  },
  {
    id: 'MW-P1-NEW-05',
    severity: 'High',
    phase: 'Phase 1 (Backend/Infra)',
    category: 'CI/CD & Testcontainers',
    fileLocation:
      'backend/tests/ShopVerse.IntegrationTests/Api/ApiStartupIntegrationTests.cs & .github/workflows/ci.yml',
    title: 'Integration Tests Only Probe `/health/live` and Never Test `/health/ready`, Security Headers, or Correlation IDs',
    flawExplanation:
      'Phase 1 Deliverable #5 requires integration tests verifying API readiness, middleware security contracts, and infrastructure health. Currently, `ApiStartupIntegrationTests.cs` has only 2 tests (`HealthLive_ReturnsOkAndHealthy` and `UnknownRoute_ReturnsNotFound`) and never asserts `SecurityHeadersMiddleware` (`Content-Security-Policy`, `X-Content-Type-Options`, `X-Frame-Options`), `CorrelationIdMiddleware` (`X-Correlation-Id` reflection and sanitization of malicious headers), or `/health/ready` JSON contract.',
    impact:
      'Regressions in security headers, correlation ID sanitization, or readiness probe serialization pass CI undetected.',
    language: 'csharp',
    exactCodeFix: `[Fact]
public async Task SecurityHeaders_And_CorrelationId_Sanitization_AreEnforced()
{
    var client = _factory.CreateClient();
    client.DefaultRequestHeaders.Add("X-Correlation-Id", "bad_header_with_spaces_and_crlf\\r\\n");

    var response = await client.GetAsync("/health/live");

    response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).Should().BeTrue();
    nosniff!.First().Should().Be("nosniff");
    response.Headers.Contains("Content-Security-Policy").Should().BeTrue();

    // Malicious Correlation-Id must be rejected and replaced with a clean 32-char GUID
    response.Headers.TryGetValues("X-Correlation-Id", out var corrId).Should().BeTrue();
    corrId!.First().Should().MatchRegex("^[a-f0-9]{32}$");
}`
  },
  {
    id: 'MW-P2-NEW-06',
    severity: 'Medium',
    phase: 'Phase 2 (Design System/UI)',
    category: 'Tailwind & Design Tokens',
    fileLocation:
      'frontend/packages/ui/src/components/Badge.tsx (Lines 38–51) & docs/02_DESIGN_SYSTEM.md (Line 45)',
    title: '`Badge.tsx` Uses Hardcoded Dark-Mode Palette Classes That Fail WCAG AA Contrast in Light Mode',
    flawExplanation:
      '1) `Badge.tsx` uses raw Tailwind palette classes (`bg-amber-500/15 text-amber-400`, `text-sky-300`, `text-emerald-400`, `via-indigo-500/20 to-purple-500/20`) that slipped past `lint-tokens.mjs` because the regex only checked `red|blue|green|yellow|purple|pink|gray|slate|zinc|neutral|stone`. On Light Mode `#FFFFFF` surfaces, `text-amber-400` (`#FBBF24`, 1.67:1 contrast) and `text-sky-300` (`#7DD3FC`, 1.54:1 contrast) are completely illegible and fail WCAG 2.2 AA (4.5:1 required). 2) `lint-tokens.mjs` must also ban `amber|sky|emerald|indigo|violet|rose|teal|cyan|orange|lime|fuchsia`.',
    impact:
      'B2B Verified, Industrial Grade, and Low Stock badges are unreadable in Light Mode and violate the Zero-Raw-Color token gate.',
    language: 'typescript',
    exactCodeFix: `// In frontend/packages/ui/src/components/Badge.tsx:
        warning: 'bg-warning/15 text-warning border-warning/30',
        fault: 'bg-fault/15 text-fault border-fault/30 animate-pulse',
        deal: 'bg-deal text-surface border-transparent font-bold tracking-wider',
        'industrial-grade': 'bg-bg-muted text-warning border-warning/50 font-mono',
        'b2b-verified': 'bg-brand-blue/15 text-accent border-accent/40',
        'flash-deal': 'bg-deal/20 text-deal border-deal/50 font-bold',
        'free-shipping': 'bg-online/15 text-online border-online/30',
        'low-stock': 'bg-warning/15 text-warning border-warning/40',
        'ai-recommended': 'bg-accent/15 text-accent border-accent/40',`
  },
  {
    id: 'MW-P2-NEW-07',
    severity: 'Medium',
    phase: 'Phase 2 (Design System/UI)',
    category: 'Accessibility & UX',
    fileLocation:
      'frontend/packages/ui/src/components/PhoneInput.tsx (Lines 40, 87–90) & frontend/packages/ui/src/motion/motionPresets.ts',
    title: '`PhoneInput` Ignores Controlled `value` Prop Updates & Country Switch Does Not Emit `onChange`',
    flawExplanation:
      '1) In `PhoneInput.tsx`, `rawNumber` state is initialized once (`React.useState(value ?? defaultValue)`) and never synchronized when a controlled `value` prop changes (e.g., form reset or autofill). Furthermore, changing the country `<select>` updates `selectedCountry` state without firing `onChange`, leaving parent forms with the old country dial code. 2) In `motionPresets.ts`, Framer Motion variants do not expose a reduced-motion helper or respect `prefers-reduced-motion: reduce` as required by Design System §2.5.',
    impact:
      'React Hook Form / controlled checkout forms submit stale country prefixes or fail to reset `PhoneInput` values.',
    language: 'typescript',
    exactCodeFix: `// In frontend/packages/ui/src/components/PhoneInput.tsx:
    React.useEffect(() => {
      if (value !== undefined) {
        setRawNumber(value);
      }
    }, [value]);

    const handleCountryChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
      const country = COUNTRIES.find((c) => c.code === e.target.value) || COUNTRIES[0];
      setSelectedCountry(country);
      const clean = rawNumber.replace(/\\D/g, '');
      const isValid = country.regex.test(clean);
      onChange?.(\`\${country.dial}\${clean}\`, isValid);
    };`
  },
  {
    id: 'MW-P0-NEW-08',
    severity: 'Low',
    phase: 'Phase 0 (Docs/Schema)',
    category: 'Schema & Spec Consistency',
    fileLocation:
      'docs/DATA_MODEL.md (Lines 184–188) & docs/02_DESIGN_SYSTEM.md (Line 45) & frontend/packages/ui/tokens/colors.ts (Line 76)',
    title: 'Duplicate Conflicting `ix_otp_codes_lookup` Index in `DATA_MODEL.md` & Light-Mode `--focus-ring` Drift',
    flawExplanation:
      '1) In `docs/DATA_MODEL.md` lines 184–188, the `otp_codes` table has two consecutive `**Indexes:**` blocks defining the same index name `ix_otp_codes_lookup` with two different column signatures (`(recipient, type, reference_id, is_consumed)` vs `(recipient, type, is_consumed)`). Executing both DDL statements in PostgreSQL fails with `relation "ix_otp_codes_lookup" already exists`. 2) `docs/02_DESIGN_SYSTEM.md` line 45 and `tokens/colors.ts` line 76 list Light Mode `--focus-ring` as `#00A8B5`, whereas `globals.css` line 54 and `verify-contrast.mjs` use `#00707D` (5.55:1 AA contrast).',
    impact:
      'Schema migration ambiguity on `otp_codes` and token documentation drift between `colors.ts` and `globals.css`.',
    language: 'sql',
    exactCodeFix: `-- 1. In docs/DATA_MODEL.md (Lines 184-188), consolidate into a single composite index:
CREATE INDEX ix_otp_codes_lookup
ON otp_codes (recipient, type, reference_id, is_consumed);

-- 2. In docs/02_DESIGN_SYSTEM.md (Line 45) & frontend/packages/ui/tokens/colors.ts (Line 76):
-- Update Light Mode focusRing from '#00A8B5' to '#00707D' to match globals.css and WCAG AA (5.55:1).`
  }
];
