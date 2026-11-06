# Threat model: company accounts, credit and approvals (draft for human review)

> Pricing, Quotes, Payments and Invoicing are covered in [v2](THREAT_MODEL_PRICING_QUOTES_PAYMENTS_INVOICING.md).

> Status: **draft by an AI assistant, 2026-10-30. Not reviewed by a person, not a penetration test.**
> Brief v2 §11.1 asks for a STRIDE threat model per module before it is built; this one was written after the
> modules were built (PR #18), so treat the "mitigations" as claims to be checked, not as sign-off.
> Scope: `Companies` (accounts, sites, cost centres, members, credit), `Approvals`, and the sign-in / session
> code that guards them in `apps/web` and `apps/console`. Out of scope: catalogue, inventory, payments, orders.

## How each statement was checked

| Mark | Meaning |
|---|---|
| **RUN** | Reproduced by running the system |
| **TEST** | Proven by an automated test (name given) |
| **CODE** | Read in the source only; nothing executes it |

A **CODE** claim is the weakest. A reviewer should look at those first.

## Severity scale
**Critical**: an outsider gains admin or breaks tenant isolation today. **High**: a realistic abuse or a
brief requirement that is missing, must be fixed before real customers. **Medium**: real but needs an insider or
unusual conditions. **Low / Info**: hardening or a rule for future work.

---

## 1. Findings register (read this first)

### C1. Known admin account exists in every environment (Critical) [RUN] — **FIXED**, see "Fix" below
`IdentityService` calls `SeedEnterpriseAccounts()` unconditionally in its constructor
(`backend/src/ShopVerse.Infrastructure/Identity/IdentityService.cs`). It creates, with passwords and secrets that
are **in this public repository**: `admin@mechware.com.bd` / `MechAdmin#2026!` with TOTP secret
`JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP` and recovery code `ABCDE-12345`, plus seller and buyer accounts.
**Reproduced:** the API started with `ASPNETCORE_ENVIRONMENT=Production` and a fresh random JWT secret
accepted that password (HTTP 202, 2FA required), then accepted a TOTP computed from the public secret
(HTTP 200, role `admin`, permission `companies.credit` present). The existing Production boot guard only
rejects the published *JWT secret*; it does not touch these accounts.
Impact: anyone can verify companies, suspend them and grant credit on any deployment built from this code.
Fix direction: seed only in Development / Test (and refuse to boot if a seeded e-mail exists in Production),
move staff provisioning to a one-time out-of-band step, add a test that Production has no seeded accounts.

**Fix (same day):** demo accounts are now created only when the environment is `Development`
(`IdentitySeedPolicy.ForEnvironment`, decided from the environment name alone; no setting can switch them on in
Production or Staging). `IdentityService` takes the policy as a required constructor argument. The startup log states
which case applies.
**Evidence:** (1) the same attack re-run on a Production-mode API now answers HTTP 401 at both steps (password,
then password + TOTP from the public secret) [RUN]; (2) tests `DemoAccountSeedingTests` (unit, 11) and
`Production_HasNoDemoAccounts_*`, `Staging_HasNoDemoAccountsEither`, with the control
`Development_SeedsTheDemoAccounts_*` (integration) [TEST]; (3) **mutation-checked:** with the old unconditional
seeding restored, 2 unit and 2 integration tests fail, with the fix they pass.
**Consequences and what is still open:**
- Production and Staging now start with **no staff or buyer accounts at all**, so nobody can sign in to the console
  there until identity is database-backed and there is a provisioning step for the first administrator. That is
  deliberate (fail closed) but it is a task to schedule.
- The environment name is now the only switch. A public host started with `ASPNETCORE_ENVIRONMENT=Development`
  would re-create the accounts (and also enables Swagger and accepts the published development JWT secret).
  Deployment configuration must never use that value; an optional extra guard would refuse to listen on non-loopback
  addresses in Development.
- The browser E2E and CI run the API in Development, so they are unaffected.

### C2. Google sign-in believed any token, so anyone could sign in as anyone (Critical) [TEST] — **FIXED 2026-11-05**
Found while auditing the 2FA paths. `POST /auth/google` never checked the ID token's signature or audience:
- **Any self-made token was accepted.** A three-part token with `"iss":"https://accounts.google.com"` and any `email` was
  enough.
- **A "mock" format let the attacker name the account.** `{"idToken":"google_oidc_x","expectedEmail":"victim@…"}` took
  the e-mail straight from the request body.
- **The result:** a session as any account without 2FA, and a Google link to it. Missing `email_verified` was also read
  as yes.

**Fix:**
- **The token must be Google's own.** `GoogleIdTokenVerifier` checks:
  - the RS256 signature, against Google's published keys (JWKS);
  - the issuer, the audience (`aud` = `Authentication:Google:ClientId`) and the expiry (60 s skew; `iat` not in the
    future);
  - that `email_verified` is `true`;
  - a size cap of 8 KB.
- **Claims are read only after the signature checks out.** There is no test or mock path any more. `GoogleLoginRequestDto` carries
  only `idToken`.
- **No client id, no Google sign-in.** Without a configured client id the endpoint answers `403 GOOGLE_SIGNIN_DISABLED`.
- **Key caching (`GoogleJwksSigningKeys`):**
  - cached for as long as Google's `Cache-Control` says;
  - an unknown key id forces at most one refresh a minute;
  - during an outage the last good keys keep working;
  - with no keys at all it fails closed, and callers get that answer at once for 10 s instead of each waiting on Google.

**Evidence:**
- TEST: `IT/Identity/GoogleIdTokenVerifierTests.cs` (21 cases):
  - another key, a tampered claim, `alg` none / HS256 / RS512, an unsigned token, the old mock formats;
  - an unknown `kid`, the wrong `aud` / `iss`, expired / no `exp` / non-numeric `exp`, `iat` in the future, an unverified
    e-mail, an oversized token;
  - the JWKS parser skipping bad entries.
- TEST: `GoogleJwksSigningKeysTests` (4): caching, the refresh limit, failing closed with a fast back-off, the last good keys
  during an outage.
- **Mutation check:** every guard removed one at a time is caught.

### H5. One-time codes and Google skipped the second factor (High) [TEST] — **FIXED 2026-11-05**
`VerifyOtpAsync` and `GoogleLoginAsync` issued a full session without asking for the TOTP. That held even for accounts
with 2FA turned on and for admin / finance, whose role demands it. So whoever could read the phone or mailbox, or had the
Google account, got past the authenticator. Further gaps:
- **A password-reset code worked as a sign-in.** `VerifyOtpAsync` did not check the code's type.
- **Google ignored the account state.** It did not check the lockout, linked by e-mail to accounts that had never proven
  that e-mail (an attacker could pre-register the victim's address), and quietly moved an account to a second Google
  identity.
- **Suspended accounts were not refused.** No sign-in path refused `Suspended`, and refresh did not either.

**Fix:**
- **OTP sign-in:**
  - only phone, e-mail and login codes are looked up, so a reset code is never found there and is not spent by the try;
  - a correct code still verifies the contact;
  - an account that needs two factors then gets `403 PASSWORD_AND_2FA_REQUIRED` instead of a session.
- **Google sign-in:**
  - finds the account by Google identity first, then by e-mail;
  - refuses a second Google identity (`409 GOOGLE_ACCOUNT_MISMATCH`);
  - refuses a locked account;
  - refuses an account that needs 2FA, without linking it;
  - refuses to link an account whose e-mail was never verified (`409 EMAIL_NOT_VERIFIED`);
  - turns a double-click race into `409 GOOGLE_SIGNIN_IN_PROGRESS` instead of a 500.
- **Every sign-in path:** password, OTP, Google and refresh all refuse `Suspended` / `Banned` (`403 ACCOUNT_DISABLED`).
- **Web:** after a correct code on a 2FA account, the form goes back to sign-in and explains why.

**Evidence:**
- TEST: `IT/Identity/PasswordlessTwoFactorTests.cs` (13 tests). The 8 that reproduce the bypass failed before the fix.
  They cover:
  - a user with 2FA on;
  - a finance user without 2FA, where the role alone must demand it;
  - a reset code used to sign in;
  - lockout;
  - squatted and unverified e-mails;
  - a second Google identity;
  - suspended and banned accounts on every route.
- **Mutation check:** 18 + 11 mutants, all caught.

### H6. A stolen access token could replace an active authenticator (High) [TEST] — **FIXED 2026-11-05**
`POST /auth/2fa/setup` replaced the TOTP secret and the recovery codes at once, even when 2FA was already on. Anyone holding a
15-minute access token, including an admin's, could:
- lock the owner out of their own authenticator;
- keep the new secret and recovery codes for later.

**Fix:** setup is refused with `409 TWO_FACTOR_ALREADY_ENABLED` once 2FA is on.

**Evidence:**
- TEST: `SetupCannotReplaceAnAuthenticatorThatIsAlreadyOn`.
- **Mutation check:** caught.

### H1. Approvals enforce nothing yet, and the amount is client-supplied (High, design rule for Phase 8) [CODE] — **FIXED 2026-11-05**, see "Fix" below
`ApprovalService.SubmitAsync` trusts `amountMinor` from the caller, and when no rule matches it only returns
`approvalRequired: false`: nothing stops an order from proceeding without approval, because orders do not
exist. Rules for Phase 8: (1) the server computes the total from the cart/order, never from the client; (2) order
placement itself must refuse to proceed while an approval is pending / rejected / absent when a rule matches;
(3) the approval request must be created in the same transaction as the order and reference it.

**Fix (2026-11-05):** all three rules hold.
- **Placement:** a company order is matched against the company's active approval rules with the **server's own total**,
  plus the optional cost centre (which must belong to the company, else 404) and the order's categories. When a rule
  matches, the order starts `pending_approval`, and its `ApprovalRequest` (subject `order`, amount = grand total) is
  created in the **same transaction** as the order.
- **No paying around it:** an order waiting for approval cannot be paid (`ORDER_NOT_PAYABLE`). A credit order holds
  **no credit until approved**.
- **Approved:** the order becomes payable, and its 30 minutes to pay (`orders.payment_due_at`) start at the approval.
  A credit order is confirmed with the credit held then; if the credit no longer covers it, it is cancelled rather than
  overdrawn.
- **Rejected or withdrawn:** the order is cancelled and its stock freed.
- **Nobody decides within 24 hours:** the order lapses and its request leaves the inbox. Stock is held for up to
  24 h 30 min (ADR 0004 amendment).
- **Missed decision:** a decision that was not applied at once (crash) is applied by the minute sweep.
- **Buyer cancels a waiting order:** its request is withdrawn too.
- **Open-order cap:** orders waiting for approval count towards it.
- **No client-made order approvals:** `POST …/approvals` refuses subject type `order`, so a client cannot create an
  order approval with its own amount.

**Evidence:**
- TEST: `IT/Orders/OrderApprovalTests.cs` (10 tests).
- **Mutation check:** 9 mutants, all caught:
  - no rule lookup
  - credit held at placement
  - payment clock not restarted
  - decision not applied
  - no 24-hour lapse
  - cap ignoring waiting orders
  - order subject accepted from clients
  - client-side amount
  - request not withdrawn on cancel

### H2. Company-registration abuse and BIN squatting (High) [CODE] — **FIXED 2026-11-05** (KYC documents still open)
Any signed-in user can register any number of companies (`CompanyService.RegisterAsync`, no per-user cap, only
the global 100 requests/min/IP limit). BIN is globally unique (`ux_companies_bin`, `COMPANY_BIN_EXISTS`), so
someone can register a real firm's BIN first and lock the real firm out. Staff can only verify or **suspend**; there is
no reject / release / dispute, and no document check. Fix direction: a per-user cap and a registration rate
limit, staff "reject and release BIN", KYC document upload before a BIN is held, and a way for the real owner to
dispute.

**Fix (2026-11-05):**
- **Reject frees the BIN:** staff (`companies.manage`) can reject a pending registration with a reason, via
  `POST /admin/companies/{id}/reject`.
  - The company becomes `rejected`; the registrant sees the reason.
  - Its **BIN is freed**: `ux_companies_bin` is now a partial unique index that skips rejected rows.
  - The real owner can then register; the rejected record stays for the audit trail.
- **A rejected company is dead:** it cannot be activated, suspended or used (`COMPANY_REJECTED` on every member write).
- **Pending cap:** one account may hold at most 2 unverified registrations (`TOO_MANY_PENDING_COMPANIES`). A per-registrant
  transaction lock keeps the count exact under parallel requests.

**Evidence:**
- TEST: `IT/Companies/CompanyRegistrationAbuseTests.cs` (6 tests, including racing firms for a released BIN and parallel
  registrations by one account), `UT/Payments/RefundRulesTests.RejectingACompany_NeedsCompaniesManage`.
- **Mutation check:** 6 mutants, all caught:
  - BIN check ignores rejection
  - no pending cap
  - rejected company activatable
  - rejected company usable
  - active company rejectable
  - no lock (fails 3 out of 3 runs)

**Still open:**
- KYC document upload before a BIN is held.
- A dispute path for the real owner (they contact MECHWARE today).

### H3. No step-up or second person for credit changes; audit is not tamper-evident (High) [CODE] — **FIXED 2026-11-06** (step-up and tamper-evident audit)
Brief §11.1 requires step-up authentication for credit limits. `PUT /admin/companies/{id}/credit` needs only
`companies.credit` plus the login-time TOTP. Every change is recorded (before / after, actor, IP) by
`AuditLogSaveChangesInterceptor` in the same transaction [CODE], but the log is an ordinary table: no hash chain, no
write-once anchor (brief §6.11), so someone with database write access can alter history.

**Step-up fix (2026-11-06):** sensitive staff actions now need a fresh authenticator code in `X-Step-Up-Code`, checked
just before the action, after the permission check.
- **Which actions:** changing a company's credit, approving a refund, and issuing a 2FA setup code.
- **What counts as a code:** only a current TOTP code, not a recovery code. Its time step is spent, so one code confirms
  one action.
- **The audit records the target.** The entry names what the code confirmed (company, refund or user).
- **Wrong codes are counted apart from the login lockout,** so a stolen session guessing codes cannot lock the owner out.
  The fifth wrong code in a row ends every session instead.
- **A correct code clears earlier misses.**
- **Console:** the credit dialog asks for the code and shows the API's own message.

**Residual:** the code is spent even when the action itself then fails, so a retry needs the next code (up to 30 s).

**Evidence:**
- TEST: `IT/Api/StepUpTests.cs` (4 tests).
- E2E: no code, a wrong code, then the next code.
- **Mutation check:** 8 mutants, all caught.

**Tamper-evident audit (2026-11-06):** the audit log is now append-only and sealed into a hash chain.
- **Append-only.** A trigger blocks `UPDATE`, `DELETE` and `TRUNCATE`. The only update allowed is sealing a row, once, and it
  cannot touch the content.
- **Sealed into a hash chain.** Each minute `AuditChainWorker` runs `seal_audit_logs()`. Each sealed row stores the previous
  row's hash and SHA-256(previous hash ‖ canonical JSON of its content); timestamps are in microseconds, so the session time
  zone cannot change them.
- **Tampering is detected where it happened.** `verify_audit_chain()` finds an edited, removed or reordered row even when a
  database owner switched the trigger off; it runs daily and on request (`GET /admin/audit/chain`, `audit.view`, cached for
  5 minutes).
- **The head is anchored outside the database.** The chain head is logged every minute, and the worker raises a critical
  alarm if the head ever goes back or changes. That catches a truncated or consistently rewritten tail, which the chain
  alone cannot show.

**Residual:**
- Rows are sealed 30 s to a minute after they are written; until then the trigger alone protects them.
- The anchor is only as safe as the log store it is shipped to.

**Evidence:**
- TEST: `IT/Security/AuditChainTests.cs` (19 cases).
- TEST: `IT/Api/AuditChainEndpointTests.cs`.
- Unit: `AuditChainHeadTests`.
- **Mutation check:** 10 mutants, all caught.
- The migration runs in `CatalogHardeningTests` (`MigrateAsync`).

### H4. Business identifiers stored in clear and copied into the audit log (High vs brief) [TEST] — **FIXED 2026-11-06**
BIN, TIN, trade-licence number and legal name are plain columns. Nothing in the code base encrypts at
application level (brief §11.1 "PII and KYC numbers encrypted at application level"), and the audit interceptor
writes the full old / new values of every changed row, including these fields, as JSON. A database dump or a
read-only audit viewer exposes them. Fix direction: encrypt these columns with a managed key, exclude or mask
them in audit values, restrict who can read `audit_logs`.

**Fix:** the identifying numbers BIN, TIN and trade licence are now encrypted at application level, in line with brief §11.1.
- **Encryption.** AES-256-GCM with a random nonce, through EF value converters (`FieldProtector`). The columns hold
  `enc:v1:` ciphertext and the entity holds plain values. A changed byte fails to decrypt.
- **Keys.** Encryption and index keys are derived with HKDF from one 32-byte master key, `Encryption__FieldKey`. Outside
  Development the API refuses to start without it, or with the published development key.
- **BIN lookups use a keyed fingerprint.** Equal BINs no longer look equal in the database, so uniqueness (the partial
  index, now on `bin_index`) and the "BIN taken" check use HMAC-SHA256 under a second key. `ShopVerseDbContext` refreshes it
  on every save.
- **Never query `c.Bin == x`.** With randomized encryption that comparison silently matches nothing. One test did exactly
  that and was fixed; the rule is written in `CompanyBinIndex`.
- **The audit log no longer copies them.** BIN, TIN, trade licence and the fingerprint are `[REDACTED]` in old/new values.
- **Rows written before encryption** are read as they are, and a start-up backfill encrypts and indexes them.
- **Legal name stays in clear.** It is public business information and is searched.

**Residual:**
- Key rotation is not built: the format is versioned (`v1`) but holds one key.
- `audit.view` holders still read other audit values.

**Evidence:**
- TEST: `IT/Companies/CompanyFieldEncryptionTests.cs` covers ciphertext in the database and plain values in the app, no
  identifiers in the audit log, and the backfill.
- TEST: the start-up guard (2 cases).
- Unit: `FieldProtectorTests` (8).
- **Mutation check:** started on 9 mutants but not finished (stopped mid-run); the guards are covered by the tests above.

### M1. Staff roles other than admin / finance sign in without a second factor (Medium vs brief) [TEST] — **FIXED 2026-11-05** (passkeys still open)
`RequiresMandatoryTwoFactor` covered admin and finance only. `support` holds `companies.view` and could read every
company's legal name, BIN and TIN with a password alone. Brief §11.1 asks for passkeys for all staff.

A second gap sat underneath: a staff member whose role demanded 2FA but who had no authenticator could never sign in. 2FA
setup needed a session, so there was no way to enrol the first authenticator.

**Fix:**
- **Every staff role needs an authenticator:** admin, finance, support and moderator.
- **The first authenticator needs a code from another administrator.**
  - An administrator (`users.manage`, signed in with 2FA) issues a one-time **setup code** for that person. You cannot
    issue one for yourself.
  - The code is 10 characters (50 bits), valid for 72 hours, good for 3 tries, and handed over in person.
  - A new code cancels earlier codes and open tokens.
- **The password alone earns nothing.** Password + setup code returns the new secret and recovery codes (shown once) and a
  10-minute, single-use token.
- **Running out of time is recoverable.** The setup code stays usable until enrolment succeeds, so someone who runs out of
  time can start again with the same code. Each new start retires the earlier token.
- **The session starts only after the first correct authenticator code.** Wrong setup codes and wrong authenticator codes
  both count towards the account lockout.
- **Old sessions do not survive the rule.**
  - When a role starts demanding 2FA, refresh revokes every session that began without it, not just refuses it.
  - Completing enrolment revokes every earlier session.
  - So a session that never passed a second factor cannot come back later.
- **No self-service enrolment for staff.** `/2fa/setup` and `/2fa/verify` refuse staff
  (`403 STAFF_ENROLL_WITH_SETUP_CODE`), so an old session or a stolen token cannot get around the setup code.
- **No code for a disabled account.** No setup code is issued for a suspended or banned account.
- **Console:** sign-in → setup code → key + recovery codes → first code.

**Residual:**
- Passkeys (WebAuthn) are not built.
- There is no console screen yet for administrators to issue codes; it is API only.
- Staff roles are still assigned in the database, because there is no staff-management feature.

**Evidence:**
- TEST: `IT/Identity/StaffTwoFactorEnrollmentTests.cs` (22 tests).
- **Mutation check:** 22 mutants on the design, plus 11 on the code-review fixes, all caught.
- **Code review:** two passes. The first found the trust-on-first-use flaw (a leaked password could enrol an attacker's
  phone), which led to the setup-code design.

### M2. Members are added without consent (Medium) [TEST] — **FIXED 2026-11-06**
A company admin could attach any user to their company, or name an arbitrary person as an approver so requests stall on
them (a form of denial of approvals), and the user could not decline. The existence part was fixed earlier:
`company_members.user_id` has a foreign key to `users`.

**Fix:** a membership is an **invitation** until its holder accepts it (`company_members.accepted_at`).
- **An invitation grants nothing.** Every "who is a member" query goes through one filter, `CompanyAccess.Effective()`
  (active and accepted). That covers:
  - company access;
  - "my companies";
  - the last-admin rule;
  - the pending-registration cap;
  - approver eligibility for rules, requests and order approvals.
- **No invitation-based blocking.** Inviting someone as an admin of pending companies cannot block their own registrations.
- **Consent covers the roles offered.** Giving someone else a new role (say, Approver) turns the membership back into an
  invitation for the new roles. Removing a role or changing a limit needs no new consent, and an admin changing their own
  roles has consented by doing it.
- **Answering is the invitee's alone.** The invitee accepts or declines (declining removes it); nobody else can answer, and
  an accepted membership cannot be "declined".
- **The admin can withdraw.** Switching an unanswered invitation off withdraws it.
- **Existing members** were backfilled as accepted.
- **Web:** the invitee sees invitations on their companies page with Accept / Decline, and the admin's team list marks
  "Invited".

- **A "no" is remembered** (2026-11-06, follow-up). Declining records `company_invitation_declines`, and the same company
  cannot invite the person again for 30 days (`409 INVITATION_DECLINED`); a second "no" restarts the period. Other
  companies are not affected.

**Residual:** there is no invitation e-mail yet.

**Evidence:**
- TEST: `IT/Companies/MemberInvitationTests.cs` (13 tests).
- TEST: `OrderApprovalTests.AnApproverWhoHasNotAcceptedTheMembership_IsNeverAskedToApproveAnOrder`.
- Unit: `AnInvitation_GrantsNothing_UntilAccepted_AndIsAcceptedOnce`.
- E2E: the admin sees "Invited"; the buyer accepts in their own browser before the rule can name them.
- **Mutation check:** 16 mutants, all caught. `/code-review`: 4 findings fixed; the residual is noted above.
- **Mutation check (follow-up):** 5 mutants on the decline memory, all caught (one survived first and got a test).

### M3. Spend limits and cost-centre assignment are stored but not enforced (Medium) [CODE] — **FIXED 2026-11-05**
A requester can submit against any cost centre of the company (`SubmitAsync` only checks it belongs to the
company, not that the member is assigned to it). `CompanyMember.SpendLimitMinor` and `CostCentre.MonthlyBudgetMinor`
are never compared with anything.

**Fix (2026-11-05):**
- **Cost-centre assignment:** orders carry a cost centre (`orders.cost_centre_id`). A member assigned to cost centres must
  name one of them, both for an order and for an approval request (`COST_CENTRE_NOT_ASSIGNED`). A member with no
  assignment may use any of the company's cost centres, or none.
- **Limits only via approval:** above the member's spend limit (`SPEND_LIMIT_EXCEEDED`) or the cost centre's monthly
  budget (`BUDGET_EXCEEDED`), an order goes ahead only through a matching approval rule; with no rule it is refused.
- **What the budget counts:** the Bangladesh calendar month, and every non-cancelled order (pending ones too, since they
  may still go through).
- **No races:** a per-cost-centre lock, taken after the buyer's lock, stops two buyers fitting under the same budget at
  once.

**Evidence:**
- TEST: `IT/Orders/CompanySpendControlTests.cs` (6 tests, including racing buyers and the Dhaka month boundary).
- **Mutation check:** 8 mutants, all caught:
  - assignment ignored, for orders and for approval requests
  - spend limit ignored
  - budget ignored
  - cancelled orders counted
  - UTC month
  - no cost-centre lock (fails 3 out of 3 runs)
  - limits applied even when a rule routes the order

### M4. Staff permissions live in the access token for up to 15 minutes (Medium) [TEST] — **FIXED 2026-11-05**
Permission claims came from the JWT (`PermissionAuthorizationHandler`), so removing a staff permission or role took effect
only when the token expired. The same 15-minute delay applied to more than roles:
- suspending, banning or erasing an account;
- signing out;
- a password reset;
- "sign out everywhere".

Company roles were already read from the database on every call.

**Fix:**
- **Every access token names its session.** It carries `sid`, its refresh-token family, and a token without one is refused.
- **Every request is checked against the database.** `JwtBearerAuthenticationHandler` asks `SessionValidator` (one query)
  whether:
  - the account exists and is not suspended, banned or erased;
  - the session still has a usable token (not revoked, not used, not expired);
  - a staff role has not left the account without its required authenticator.
- **Current values replace the token's.** Roles, permissions and the seller link are rebuilt from the database, not taken
  from the token.
- **One set of rules.** `AccountRules` (Domain) holds the account rules that login, refresh and this check share.

Fixed along the way (found by the code review):
- **Revoking one token left the session open.** `sessions/revoke` with one token's id left the rest of the family alive,
  so the access token survived. A session is now always revoked as a whole family.
- **Logout without the cookie signed out every device.** A bearer-only client, or a blocked cookie, revoked every session.
  It now ends only the token's own session, and "end this session" with no known session ends none.

**Evidence:**
- TEST: `IT/Api/LiveSessionTests.cs` (14 tests, over HTTP). They cover:
  - logout, and one device versus another;
  - suspended, banned and erased accounts;
  - a removed role and a changed seller link;
  - a staff role without an authenticator;
  - a forged token for an unknown account, and an unknown session;
  - revoke by a used token after a refresh;
  - bearer-only logout.
- **Mutation check:** 21 mutants, all caught.

**Cost:** one indexed query per authenticated request. A database outage shows as `500`, not `401`, so clients do not
throw away good sessions.

### M5. No Content-Security-Policy on the web and console apps (Medium) [TEST] — **FIXED 2026-11-05**
Neither Next.js app set a CSP at first. The first fix (2026-11-04) added one, but it still allowed
`script-src 'unsafe-inline'`. An injected inline event handler or `<script>` would therefore run. The access token lives
in memory, which stops theft from storage but not use by injected script.

**Fix:**
- **Every response gets its own nonce.** Each app's middleware creates a fresh nonce for every response and sends
  `script-src 'self' 'nonce-…' 'strict-dynamic'`, with no `'unsafe-inline'`. Next.js stamps the nonce on its own scripts.
  - Pages are rendered per request for this (`dynamic = 'force-dynamic'`).
  - The cost is server-side rendering on every hit; the benefit is that injected script cannot use the in-memory token.
- **Every public file gets the policy too.** The middleware now covers all paths except Next's hashed build output.
- **A next-intl gap is closed.** next-intl's pass-through without forwarded headers is caught, so no page renders without
  its nonce.
- **What is unchanged:** styles keep `'unsafe-inline'`, because the UI and next/font use inline styles and CSS cannot
  run code. Google sign-in, once built, must load through `next/script`.

**Evidence:**
- RUN (Playwright, production builds):
  - every server-rendered script carries the response's nonce;
  - an injected `<img onerror>` and a parsed inline `<script>` do not run;
  - under the old policy the same `onerror` handler did run.
- TEST: the E2E suites (`packages/e2e`) now check on both apps:
  - the nonce on every script;
  - no `'unsafe-inline'` for scripts;
  - the injected handler is blocked;
  - a fresh nonce per response;
  - public files are covered.
- **E2E result:** web 31/31, console 28/28.

### M6. Threshold splitting and collusion cannot be stopped in software (Medium, accepted business risk)
Rules look at one request's amount. Splitting a purchase below a threshold, or two colluding people (one
requests, the other approves, both under one admin), is not detectable here. Mitigation is reporting and the audit trail.

### L1. Credit consumption is not wired to anything yet (Low, rule for Phase 8) [CODE]
`Company.ReserveCredit` exists and is race-safe on its own (`xmin`, check constraints), but no endpoint calls it.
When checkout uses it, it must run in the **same transaction and on the same loaded company row** as order creation.
The existing concurrency test exercises the aggregate directly, not checkout.

### L3. One office behind one IP shared a single 100/min budget, and excess requests hung (Medium, availability) [TEST] — **FIXED 2026-11-05**
Found while running the browser E2E. The global limiter was per IP only (100 / min), with a queue of 10. That caused two problems:
- **A whole company shared one budget.** Behind NAT, every buyer in the office drew from it.
- **Requests over the limit hung.** They waited up to a minute instead of failing; the E2E saw a 30 s hang on an admin action.

**Fix:**
- **Authentication now runs before the limiter.** A signed-in user (verified token, live session) gets their own budget.
- **Anonymous traffic is limited per IP.** That includes a forged token.
- **No queue.** Over the limit is an immediate `429` with `Retry-After` and the usual error envelope.
- **A ceiling per IP, before authentication.** It is 3000 / min and runs before authentication (which costs a DB query per
  bearer token). A flood stops before the database, and many accounts behind one IP cannot add up past it.

**Open for the owner:** the strict auth limit is 5 / min per IP in Production, from the brief §11.1. Behind NAT it can slow a
whole office signing in at 9 am. Raising it is a trade-off against credential stuffing.

**Evidence:**
- TEST: `IT/Security/PerUserRateLimitTests.cs` (3 tests).
- **Mutation check:** 6 mutants, all caught.

### L2. Other (Low / Info)
- Session marker cookies (`mw_session`, `mw_console_session`) are forgeable by design: they only pick a redirect, the API authorises everything [CODE].
- No `Idempotency-Key` on these endpoints (brief §6.6). Double submits are blocked by a unique index and a second decision is a conflict, so the practical impact is small [TEST: `SameItem_CannotHaveTwoPendingRequests_*`].
- Global rate limit is per IP, 100 / minute with a queue: several office users behind one NAT share it (seen as 30 s stalls in E2E) [RUN].
- Lists inside a company (members, sites, inbox) are unbounded; they are company-scoped, so only a company can slow itself.
- Identity is in-memory (Phase 3): restart loses users, deleting a user leaves member rows pointing at an id with no row.

---

## 2. Assets and trust boundaries

| Asset | Where | Who may read / change |
|---|---|---|
| Company identity (legal name, BIN, TIN, licence) | `companies` | Members read; staff `companies.view`; registrant writes once |
| Credit terms and limit, credit used | `companies` | Staff `companies.credit` set; nobody sets `used` over HTTP |
| Membership and roles | `company_members` | Company admin; read by the member |
| Approval rules and requests | `approval_*` | Admin (rules); requester, chain and admins (requests) |
| Staff sessions | refresh cookie (httpOnly, path `/api/v1/auth`), access token in memory | the user |

Boundaries: browser ↔ Next.js (UX only, never authorisation: CVE-2025-29927), browser ↔ API (Bearer token, CORS
allow-list with credentials), API ↔ PostgreSQL. **Tenant boundary = `company_members`**: every company call resolves
the caller's active membership from the database through one function (`CompanyAccess.RequireMemberAsync`) and does not
use a company id from a token claim.

## 3. STRIDE by module

### 3.1 Companies and members

| | Threat | Mitigation | Evidence |
|---|---|---|---|
| S | Pretend to be a member of another company | Membership read from DB per call, not from claims | TEST `CompanyServiceTests.Idor_OutsiderSeesNothingOfAnotherCompany_EveryOperationAnswersNotFound` |
| S | Register a firm's BIN you do not own | Staff verification before `active`; **no** reject / release path | H2 |
| T | Edit another company's site / cost centre / member through your own company route | Every child lookup filters by both company id and child id | TEST `Idor_AdminOfCompanyA_CannotTouchCompanyBChildIdsViaOwnCompanyRoute` |
| T | Attach another company's cost centre to a member | Ownership check | TEST `CostCentres_ForeignIdsCannotBeAttachedToAMember` |
| T | Two admins demote each other so nobody is left | Last-admin rule under a row lock | TEST `LastAdmin_*`, `TwoAdminsDemotingEachOtherConcurrently_*` |
| R | Deny a change | Audit interceptor writes actor, IP, before / after in the same transaction | CODE; not tamper-evident (H3) |
| I | Learn that a company id exists | Non-member always gets the same 404 | TEST (same IDOR test asserts the message) |
| I | BIN / TIN exposure through DB or audit viewer | **None** (clear text) | H4 |
| D | Flood registration | Global limiter only | H2 |
| E | Member without the role changes company data | Role check, 403; suspended company blocks writes (409) | TEST `NonAdminMembers_CannotManage_*`, `SuspendedCompany_BlocksWrites_*` |

### 3.2 Credit

| | Threat | Mitigation | Evidence |
|---|---|---|---|
| T | Raise own limit | Only `PUT /admin/companies/{id}/credit` with `companies.credit`; members have no endpoint | TEST: security regression suite checks every route is protected; CODE for the permission mapping |
| T | Exceed the limit with parallel orders | `used + total ≤ limit` in the aggregate, DB check constraints, `xmin` | TEST `ConcurrentCreditChanges_NeverLoseAnUpdate_AndNeverExceedTheLimit` (30 racing), unit `RandomOperations_NeverBreakCreditInvariants` (300 × 200) |
| T | Lower the limit below what is used | Refused (`CREDIT_LIMIT_BELOW_USED`) | TEST unit `SetCredit_CannotDropBelowUsed_OrContradictTerms` |
| R | Dispute who changed credit | Audit rows | CODE; H3 |
| E | A support / finance mix-up | Permission strings; only finance and admin hold `companies.credit` | CODE |
| – | Credit used by checkout | Not built | L1 |

### 3.3 Approvals

| | Threat | Mitigation | Evidence |
|---|---|---|---|
| S | Approve as someone else | Actor from the token, step approver compared by id | TEST `ApprovalTests` chain tests |
| S/E | Approve your own request | Requester skipped in the chain, `SELF_APPROVAL` | TEST `Requester_CannotApproveOwnRequest_EvenAsAdminAndInTheChain` |
| T | Decide out of order | Only the current step's approver; `NOT_CURRENT_APPROVER` | TEST unit chain tests |
| T | Decide twice / race | `xmin` token on the request; decided steps never change | TEST `ConcurrentDecisions_ByTheSameApprover_OnlyOneWins_*` (mutation-checked: removing `xmin` fails it), unit `RandomActions_NeverBreakTheStateMachine` |
| T | Two chains for one item | Unique index on open requests per item | TEST `ConcurrentSubmitOfTheSameItem_CreatesExactlyOneRequest` |
| T | Skip approval by sending a small amount | **Not stopped**: amount is client-supplied and nothing enforces the result | H1, M6 |
| T | Rule points at someone outside the company or without the role | Validated on save; ineligible approvers skipped at submit | TEST `Rules_RejectForeignApproversAndCostCentres`, `ApproverWhoLeftTheCompany_*` |
| R | Deny a decision | Step stores decision, comment, time; request keeps rule-name snapshot; rules changes audited | CODE |
| I | See requests you are not part of | 404 unless requester, in chain, or company admin | TEST `InsideTheCompany_RolesAndVisibilityAreEnforced` |
| I | Cross-company read | Same tenant boundary | TEST `Idor_OutsiderAndOtherCompanyAdmin_SeeNothing_EveryOperationIsNotFound` |
| D | Stall requests by naming an unwilling approver | **Not stopped** | M2 |
| E | Non-admin edits rules | Admin role required | TEST `InsideTheCompany_RolesAndVisibilityAreEnforced` |

### 3.4 Sign-in and sessions (web and console)

| | Threat | Mitigation | Evidence |
|---|---|---|---|
| S | Credential stuffing | Strict auth limiter (5/min in Production), lockout with delay | CODE + existing security regression tests |
| S | Reuse a TOTP code | Replay guard per time step | TEST existing `AuthAndRbacIntegrationTests.AdminLoginWithoutTotp_ShouldReturn202RequiresTwoFactor_SucceedWithTotp_AndBlockTotpReplayInSameWindow`. (The browser E2E only proves a **wrong** code is rejected, not a replayed one.) |
| S | Buyer enters the staff console | Console refuses non-staff roles and signs them out; API enforces permissions regardless | RUN (E2E "a storefront buyer cannot use the console", "seller opening /companies is refused (403)") |
| T | Forge the session marker cookie | Marker only decides a redirect; API returns 401 without a valid token | CODE |
| I | Token theft from storage | Access token only in memory; refresh token httpOnly, path-scoped | CODE; H-class risk M5 for injected script |
| D | Refresh-token reuse | Family revocation (ADR-0006) | existing tests |
| E | Staff without 2FA | **M1 fixed**: every staff role; first authenticator only with an administrator's setup code | TEST (mutation-checked) |
| S | Skip the authenticator with an OTP, a reset code or Google | **C2 / H5 fixed**: signed Google tokens only; no passwordless session for 2FA accounts | TEST (mutation-checked) |
| S/E | Known staff account | **C1 fixed**: no demo accounts outside Development | RUN + TEST (mutation-checked) |

## 4. What the automated tests do not cover
- No negative test that `support` cannot change credit via a forged request beyond the generic "every route protected" sweep; the per-permission mapping is only read, not tested.
- No test for registration abuse (per-user cap, BIN squatting).
- No test that audit rows redact BIN / TIN (they do not).
- The browser E2E uses Development seed accounts; it proves flows, not Production hardening.

## 5. Suggested order of work
1. ~~C1~~ done. Still needed before any deployment: database-backed identity and a way to create the first administrator (Production starts empty now).
2. Phase 8 rules **H1** and **L1** written into the checkout design before coding it.
3. **H2** (registration cap, reject / release BIN) and **H4** (encrypt identifiers, mask audit values).
4. **H3** (step-up for credit, tamper-evident audit) and **M1** (second factor for all staff).
5. **M2** (invitations), **M3** (enforce spend limits and cost-centre assignment), **M5** (CSP).

## 6. Where a human reviewer should start
`CompanyAccess.cs` (the tenant boundary), `ApprovalRequest.cs` (state machine), `Company.cs` (credit rules),
`IdentityService.cs` (C1), `AuditLogSaveChangesInterceptor.cs` (H3, H4), and `PermissionAuthorization.cs` / `Permissions.cs`
(who holds `companies.*`). Then run the two integration suites: `CompanyServiceTests`, `ApprovalServiceTests`.
