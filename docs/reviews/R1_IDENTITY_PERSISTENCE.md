# R1 sign-off: identity in PostgreSQL, bootstrap administrator, company membership integrity

Scope: PR #20 (`claude/hopeful-tesla-u8fbsr`). Method: `docs/04_REVIEW_PROMPTS.md` R1 (requirements, code, design,
tests, fix). Every status below names its evidence. Nothing is "Done" without it.

## 1. Requirements

| # | Requirement (source) | Status | Evidence |
|---|---|---|---|
| 1 | A restart or a second API instance loses no account, session, OTP or address (brief §6, ADR-0006) | Done | `Accounts_AndSessions_SurviveANewServiceInstance...`; by hand: Production-mode API restarted, refresh cookie issued before the restart returned 200, `users` stayed at 1 row |
| 2 | Production has no published credentials (threat model C1) | Done | `NoDemoAccountsOutsideDevelopmentTests`, `Production_HasNoDemoAccounts_...`, hand check: demo admin 401 |
| 3 | The first administrator can be created without a published secret, and always needs a second factor | Done | `BootstrapAdministratorTests`, `Production_BootstrapAdmin_CanSignInOnlyWithPasswordAndTotp`; mutation: removing the TOTP secret fails a test |
| 4 | Refresh-token reuse revokes the whole family (ADR-0006) and survives the failed request | Done | `RefreshTokenRotation_...`; mutation: dropping the save before the throw fails it |
| 5 | Two concurrent rotations of one token have one winner | Done | `ConcurrentRotationOfOneToken_HasExactlyOneWinner`; mutation: removing `FOR UPDATE` fails it |
| 6 | OTP attempts cannot be raced past 3 (brief §11.1) | Done | `OtpCode_ShouldEnforceMaxThreeAttempts...` (sequential) and `ConcurrentWrongGuesses_CannotOutrunTheAttemptLimit` (12 parallel guesses leave exactly 3 counted attempts and the right code is then refused); mutation: removing `FOR UPDATE` from the OTP read fails it 3 times out of 3 |
| 7 | Concurrent registration of one e-mail makes one account | Done | `ConcurrentRegistrationWithOneEmail_CreatesExactlyOneAccount` (unique index is the guard) |
| 8 | Failed logins are counted and lock the account, even though the request fails | Done | `FailedLogins_AreCounted_...`, `SecurityEvents_ArePersistedInTheAuditTable`; mutation: dropping the save fails both |
| 9 | A membership names a live account (threat model M2, existence part) | Done | FK `FK_company_members_users_user_id`; `AddMember_RefusesAnIdThatIsNotAnAccount...`, `TheDatabaseItselfRefusesAMembershipWithoutAnAccount`; migration refuses old orphan rows with a clear message (checked against a database that had one) |
| 10 | Invitation and consent when adding a member (M2, rest) | Missing | Not built; tracked in the threat model |
| 11 | GDPR erasure keeps referential integrity | Partial | Anonymisation soft-deletes and `Restrict` protects memberships; addresses are still hard-deleted (acceptable under erasure, noted) |

## 2. Code review: findings and fixes

| ID | Severity | Finding | Proof | Fix | Regression test |
|---|---|---|---|---|---|
| R1-1 | High (performance, grows with users) | The login lookup compared `lower(email)`, so the unique index could not be used: every sign-in was a full table scan | `EXPLAIN ANALYZE` on 50,000 users: `Seq Scan`, 22.98 ms, versus `Index Scan using ux_users_email`, 0.07 ms | Compare the stored (already lower-cased by `User`) value directly; same for OTP lookup. Case-insensitivity for the person typing is preserved | `LoginLookup_UsesTheUniqueIndexes_AndStillIgnoresCase` (captures the SQL; fails if `lower(` returns) |
| R1-2 | Medium (security) | An unknown e-mail returned before any password hashing, a wrong password after about 100 ms of PBKDF2: response time revealed which addresses have accounts | Code path: early `throw` before `VerifyPassword` | Verify against a throw-away hash for unknown accounts | `UnknownAndKnownAccounts_CostTheSameNumberOfPasswordChecks` (counting hasher) |
| R1-3 | Medium (test integrity) | Four API tests passed locally only because a leftover default database existed; CI has none | CI run failed with `3D000: database "shopverse" does not exist`; reproduced locally by dropping that database | Those tests run over their own database (`DbBackedFactory`) | The whole suite now passes with no default database |
| R1-4 | Low | A source-grep test asserted index names inside a migration that never ran, so it proved nothing | `202610290001_Phase3IdentityAndAccess.cs` had no `[Migration]`; `dotnet ef migrations list` omitted it | Test points at the real migration and the names EF actually emits; dead file removed | `components.test.mjs` tests 24, 25 |

Reviewed and left as is (with reasons): account lockout message states the unlock time, which confirms an account exists
(only reachable after the account is already locked by guesses; changing it is a UX/security trade-off for the owner);
the `mfa_challenge_...` token is informational only (the second step re-sends the password, so it grants nothing);
no N+1 found (`Include(UserRoles)` is one query; sessions and addresses are single queries).

## 3. Design

Not applicable to this change: no screen was added or altered. The 16-viewport visual QA of the identity screens was
recorded under Phase 3 (AUD-P3-08) and the E2E suite (49 checks, including reload-restores-session) passes on this head.

## 4. Tests

Unit 272, integration 120 (Redis on, nothing skipped), E2E 49 checks, CodeQL clean. Run with no default database to
match CI. `dotnet format --verify-no-changes` clean.

## 5. Open items

A. Invitation and consent for new members (threat model M2).
B. H1 to H4 and M1, M3 to M6 in the threat model are unchanged by this PR.
