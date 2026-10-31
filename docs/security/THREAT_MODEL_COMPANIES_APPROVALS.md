# Threat model: company accounts, credit and approvals (draft for human review)

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

### H1. Approvals enforce nothing yet, and the amount is client-supplied (High, design rule for Phase 8) [CODE]
`ApprovalService.SubmitAsync` trusts `amountMinor` from the caller, and when no rule matches it only returns
`approvalRequired: false`: nothing stops an order from proceeding without approval, because orders do not
exist. Rules for Phase 8: (1) the server computes the total from the cart/order, never from the client; (2) order
placement itself must refuse to proceed while an approval is pending / rejected / absent when a rule matches;
(3) the approval request must be created in the same transaction as the order and reference it.

### H2. Company-registration abuse and BIN squatting (High) [CODE]
Any signed-in user can register any number of companies (`CompanyService.RegisterAsync`, no per-user cap, only
the global 100 requests/min/IP limit). BIN is globally unique (`ux_companies_bin`, `COMPANY_BIN_EXISTS`), so
someone can register a real firm's BIN first and lock the real firm out. Staff can only verify or **suspend**; there is
no reject / release / dispute, and no document check. Fix direction: a per-user cap and a registration rate
limit, staff "reject and release BIN", KYC document upload before a BIN is held, and a way for the real owner to
dispute.

### H3. No step-up or second person for credit changes; audit is not tamper-evident (High) [CODE]
Brief §11.1 requires step-up authentication for credit limits. `PUT /admin/companies/{id}/credit` needs only
`companies.credit` plus the login-time TOTP. Every change is recorded (before / after, actor, IP) by
`AuditLogSaveChangesInterceptor` in the same transaction [CODE], but the log is an ordinary table: no hash chain, no
write-once anchor (brief §6.11), so someone with database write access can alter history.

### H4. Business identifiers stored in clear and copied into the audit log (High vs brief) [CODE]
BIN, TIN, trade-licence number and legal name are plain columns. Nothing in the code base encrypts at
application level (brief §11.1 "PII and KYC numbers encrypted at application level"), and the audit interceptor
writes the full old / new values of every changed row, including these fields, as JSON. A database dump or a
read-only audit viewer exposes them. Fix direction: encrypt these columns with a managed key, exclude or mask
them in audit values, restrict who can read `audit_logs`.

### M1. Staff roles other than admin / finance sign in without a second factor (Medium vs brief) [CODE]
`RequiresMandatoryTwoFactor` covers admin and finance only. `support` holds `companies.view` and can read every
company's legal name, BIN and TIN with a password alone. Brief §11.1 asks for passkeys for all staff.

### M2. Members are added without consent (Medium) [CODE] (existence part fixed)
*Fixed:* a membership must name a live account: `company_members.user_id` has a foreign key to `users` and `RegisterCompany` / `AddMember` refuse an unknown or erased id (tests `AddMember_RefusesAnIdThatIsNotAnAccount_AndAnErasedAccount`, `TheDatabaseItselfRefusesAMembershipWithoutAnAccount`).
*Still open:* there is no invitation to accept.
A company admin can attach any user to their company, or name an arbitrary person as an approver so requests
stall on them (a form of denial of approvals). The invited user cannot decline.

### M3. Spend limits and cost-centre assignment are stored but not enforced (Medium) [CODE]
A requester can submit against any cost centre of the company (`SubmitAsync` only checks it belongs to the
company, not that the member is assigned to it). `CompanyMember.SpendLimitMinor` and `CostCentre.MonthlyBudgetMinor`
are never compared with anything.

### M4. Staff permissions live in the access token for up to 15 minutes (Medium) [CODE]
Permission claims come from the JWT (`PermissionAuthorizationHandler`). Removing a staff permission or role
takes effect when the token expires. Company roles are different: they are read from the database on every
call, so removing a company member is immediate [TEST: `Idor_*`, `NonAdminMembers_*`].

### M5. No Content-Security-Policy on the web and console apps (Medium) [CODE]
Neither Next.js app sets a CSP (the API does). The access token is kept in memory, which blocks theft from
storage but not use by injected script. Current mitigation: no `dangerouslySetInnerHTML` anywhere in `apps/` or
`packages/ui/src` [CODE], and React escapes text, so approver comments and company names render as text.

### M6. Threshold splitting and collusion cannot be stopped in software (Medium, accepted business risk)
Rules look at one request's amount. Splitting a purchase below a threshold, or two colluding people (one
requests, the other approves, both under one admin), is not detectable here. Mitigation is reporting and the audit trail.

### L1. Credit consumption is not wired to anything yet (Low, rule for Phase 8) [CODE]
`Company.ReserveCredit` exists and is race-safe on its own (`xmin`, check constraints), but no endpoint calls it.
When checkout uses it, it must run in the **same transaction and on the same loaded company row** as order creation.
The existing concurrency test exercises the aggregate directly, not checkout.

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
| E | Staff without 2FA | admin / finance only | M1 |
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
