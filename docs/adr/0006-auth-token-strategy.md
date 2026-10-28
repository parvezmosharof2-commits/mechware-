# ADR 0006: Dual-Token Authentication, Refresh Token Family Tracking, and RBAC

## Status
Accepted

## Date
2026-10-29

## Context
MECHWARE serves multiple user categories:
- Public storefront buyers (guest buyers, registered B2C customers, B2B procurement managers).
- External multi-vendor sellers and seller staff managing their stores.
- Internal operators (Super Admin, Ops Admin, Catalog Manager, Customer Support, Finance, Content Editor).

Authentication must withstand modern attack vectors (XSS token exfiltration, CSRF, session hijacking, brute-force credential stuffing). In accordance with OWASP ASVS Level 2, long-lived tokens must never be accessible to client-side JavaScript.

## Decision
We implement a **Dual-Token Scheme with Refresh Token Family Tracking** and **Granular Permission-Based RBAC**:

```
[ Client Request ]
       │
       ├─ Authorization: Bearer <Access JWT (15-min)>
       └─ Cookie: refresh_token=<Token Secret> (httpOnly, Secure, SameSite=Lax)
```

### 1. Token Lifecycles and Storage
1. **Short-Lived Access Token (15-minute expiry)**:
   - Cryptographically signed JWT using RSA SHA-256 (or HMAC-SHA256 with 512-bit secret).
   - Stored **only in browser memory** (React state / closure) on the frontend. Never stored in `localStorage` or `sessionStorage` (preventing XSS theft).
   - Contains claims: `sub` (User ID), `email`, `role`, `permissions` (array of granular string codes, e.g. `orders.refund`), `seller_id` (if seller).
2. **Rotating Refresh Token (7-day sliding expiry)**:
   - Cryptographically random 256-bit token string.
   - Stored in an `httpOnly`, `Secure`, `SameSite=Lax`, `Path=/api/v1/auth` cookie. JavaScript cannot read this cookie.
   - Hashed with SHA-256 before persisting into the PostgreSQL `refresh_tokens` table.

### 2. Refresh Token Family Tracking & Reuse Detection
- Each refresh token belongs to a `family_id` (UUID).
- When a refresh token is exchanged at `/api/v1/auth/refresh`:
  1. The server checks if the presented token has already been used.
  2. If the token was **already used** (indicating potential token theft and replay attack):
     - The server immediately flags a security breach.
     - **All tokens in the entire `family_id` are instantly revoked**.
     - All active sessions for that user are terminated.
     - A high-priority security audit log entry is dispatched.
  3. If the token is valid and unused:
     - The old token is marked as `is_used = true`.
     - A new refresh token is issued within the same `family_id`, and a new access JWT is returned.

### 3. Granular RBAC (Permission-Driven, Not Role-Checked)
- Code checks permissions, not raw roles:
  ```csharp
  [RequirePermission(Permissions.OrdersRefund)]
  [HttpPost("{id:guid}/refund")]
  public async Task<IActionResult> RefundOrder(Guid id, [FromBody] RefundRequestDto dto)
  ```
- Sellers are isolated via a scoped authorization handler:
  ```csharp
  if (user.IsSeller && subOrder.SellerId != user.SellerId)
  {
      return Forbid();
  }
  ```

### 4. Mandatory 2FA for Staff
- Admin, Finance, and Ops roles are required to configure TOTP 2FA (RFC 6238 via Google Authenticator / Authy) before accessing `/console`.

## Consequences
### Positive:
- Maximum defense against XSS token exfiltration.
- Automatic mitigation of refresh token theft via family revocation.
- Fine-grained permission model without hardcoded role logic in API handlers.

### Negative:
- In-memory access token requires silent refresh on page reload or before expiry.
- Frontend Next.js middleware and SSR components must route refresh calls securely via the Next.js API route proxy.
