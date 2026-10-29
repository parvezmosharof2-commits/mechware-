# REST API CONTRACT SPECIFICATION — MECHWARE

> **Base URL:** `https://api.mechware.com.bd/api/v1` (Production) / `http://localhost:5000/api/v1` (Local)
> **Specification Standard:** OpenAPI 3.1 / ASP.NET Core 10 Web API
> **Error Standard:** RFC 7807 ProblemDetails `{ "error": { "code": string, "message": string, "details": object[], "traceId": string } }`

---

## 1. Global Standards & Conventions

### 1.1 Headers
| Header | Required | Description |
|---|---|---|
| `Authorization` | Conditional | `Bearer <access_jwt_token>` for authenticated endpoints |
| `Idempotency-Key` | Mandatory for mutations | UUID v4 required on all checkout, payment, and refund POST/PUT requests |
| `X-Correlation-Id` | Optional | Client trace UUID; generated server-side if omitted |
| `Accept-Language` | Optional | `'en'` or `'bn'` (controls localized system messages) |

### 1.2 Standard Error Response Shape (RFC 7807 ProblemDetails)
```json
{
  "type": "https://api.mechware.com.bd/errors/VALIDATION_FAILED",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "The request payload failed schema validation.",
  "instance": "/api/v1/checkout/orders",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "error": {
    "code": "VALIDATION_FAILED",
    "message": "Input validation error",
    "details": [
      { "field": "shippingAddress.phone", "message": "Valid 11-digit Bangladeshi phone required." }
    ]
  }
}
```

### 1.3 Standard Pagination Query Parameters & Shape
- **Pagination Style**: Cursor-based for storefront high-velocity feeds; Page & Limit (`page`, `pageSize`) for data tables in Console.
```json
{
  "items": [],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 1420,
    "totalPages": 71,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

---

## 2. API Endpoints by Module

### 2.1 Identity & Authentication (`/auth`, `/users`, `/addresses`)

#### `POST /auth/register`
- **Auth**: Public | **Rate Limit**: 5 req/min per IP | **Idempotency**: Optional
- **Request**:
  ```json
  { "email": "engineer@squarebd.com", "phone": "+8801711223344", "password": "StrongPassword123!", "fullName": "Parvez Mosharof", "locale": "en" }
  ```
- **Response**: `201 Created`
  ```json
  { "userId": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6", "message": "Verification code dispatched via SMS and Email." }
  ```

#### `POST /auth/login`
- **Auth**: Public | **Rate Limit**: 5 req/min per IP + user | **Idempotency**: No
- **Request**:
  ```json
  { "identifier": "engineer@squarebd.com", "password": "StrongPassword123!", "totpCode": "123456" }
  ```
- **Response**: `200 OK` (Sets `refresh_token` in `httpOnly; Secure; SameSite=Lax` cookie)
  ```json
  {
    "accessToken": "eyJhbGciOi...",
    "expiresIn": 900,
    "user": {
      "id": "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
      "fullName": "Parvez Mosharof",
      "email": "engineer@squarebd.com",
      "roles": ["customer"],
      "permissions": ["orders.view_own"]
    }
  }
  ```

#### `POST /auth/refresh`
- **Auth**: Public (Reads `refresh_token` cookie) | **Rate Limit**: 20 req/min
- **Response**: `200 OK` with rotated access token & updated refresh cookie. `401 Unauthorized` (or `403 Token Family Revoked` upon reuse outside the 10-second multi-tab grace window).

#### `POST /auth/google`
- **Auth**: Public | **Rate Limit**: 10 req/min per IP
- **Request**: `{ "idToken": "eyJhbGciOiJSUzI1NiIs..." }`
- **Response**: `200 OK` (Validates Google OIDC ID token, creates/links `users.google_subject_id`, sets `refresh_token` cookie, and returns `accessToken` + `user`).

#### `POST /auth/otp/send` & `POST /auth/otp/verify`
- **Auth**: Public | **Rate Limit**: 3 req/min | **Payload**: `{ "phone": "+88017...", "type": "phone_verify" | "login" | "cod_confirm", "referenceId": "optional-uuid" }`

#### `POST /auth/password/forgot` & `POST /auth/password/reset`
- **Auth**: Public | **Rate Limit**: 3 req/min per IP
- **Forgot Request**: `{ "identifier": "engineer@squarebd.com" }` -> Dispatches time-bound reset token/OTP.
- **Reset Request**: `{ "identifier": "engineer@squarebd.com", "token": "849201", "newPassword": "NewStrongPassword123!" }` -> Rotates password hash and revokes all active `refresh_tokens` families for the user.

#### `GET /auth/sessions` & `POST /auth/sessions/revoke`
- **Auth**: Bearer (`authenticated`) | **Rate Limit**: 30 req/min
- **Revoke Request**: `{ "sessionId": "optional-family-uuid", "revokeAllExceptCurrent": false }` -> Marks target or all refresh token families as `is_revoked = true` and clears cookie on full logout.

#### `POST /auth/2fa/setup` & `POST /auth/2fa/verify`
- **Auth**: Bearer (`authenticated`, mandatory for `admin` & `finance` roles) | **Rate Limit**: 10 req/min
- **Setup Response**: `200 OK` `{ "secret": "JBSWY3DPEHPK3PXP", "qrCodeOtpAuthUrl": "otpauth://totp/MECHWARE:admin@mechware.com.bd?...", "recoveryCodes": ["MW-8F92-K19A", "..."] }`
- **Verify Request**: `{ "totpCode": "123456" }` -> Enables `two_factor_enabled = true` and stores hashed recovery codes.

#### `GET /users/me/addresses`, `POST /users/me/addresses`, `PUT /users/me/addresses/{id}` & `DELETE /users/me/addresses/{id}`
- **Auth**: Bearer (`customer`) | **Rate Limit**: 60 req/min
- **Address Request**:
  ```json
  { "label": "Factory HQ", "recipientName": "Engr. Kabir", "phone": "+8801711000000", "line1": "Plot 12, Export Processing Zone", "line2": "Building B", "area": "DEPZ", "upazila": "Savar", "district": "Dhaka", "division": "Dhaka", "postalCode": "1349", "isDefaultShipping": true, "isDefaultBilling": true }
  ```

#### `GET /locations/bd`
- **Auth**: Public (Cached 24h in Redis) | **Rate Limit**: 120 req/min
- **Response**: `200 OK` Hierarchical tree of Bangladesh `divisions` -> `districts` -> `upazilas` (with English & Bengali names and calculated `shippingZone`).

#### `GET /users/me/export` (GDPR Data Portability)
- **Auth**: Bearer (`customer`) | **Rate Limit**: 1 req/hour
- **Response**: `200 OK` JSON bundle containing all profile data, order history, addresses, and audit records.

#### `DELETE /users/me` (Right to Erasure / Anonymization)
- **Auth**: Bearer (`customer`) | **Rate Limit**: 1 req/day | **Idempotency**: Mandatory
- **Response**: `200 OK` `{ "status": "anonymized", "message": "Personal identifiable information scrubbed." }`

---

### 2.2 Seller Onboarding & KYC (`/sellers`)

#### `POST /sellers/onboard`
- **Auth**: Bearer (`user`) | **Rate Limit**: 5 req/hour
- **Request**:
  ```json
  {
    "storeName": "Alpha Automation & Robotics",
    "description": "Authorized Siemens & Omron Industrial Integration Partner",
    "plan": "pro"
  }
  ```
- **Response**: `201 Created` `{ "sellerId": "uuid", "slug": "alpha-automation-robotics", "status": "pending" }`

#### `POST /sellers/me/documents` (Private KYC Upload)
- **Auth**: Bearer (`seller`) | **Rate Limit**: 10 req/min
- **Request**: Multipart Form Data (`docType`: `'nid'` | `'trade_license'` | `'tin_certificate'`, `file`: PDF/Image)
- **Security Validation**:
  - File size capped at strictly **10MB**.
  - Magic-byte signature verification: PDF (`%PDF` / `25 50 44 46`), PNG (`\x89PNG` / `89 50 4E 47`), JPEG (`\xFF\xD8\xFF`).
  - Image files are automatically re-encoded via ImageSharp / imgproxy to strip EXIF GPS metadata before storage.
- **Behavior**: Direct server-side streaming to isolated private S3 bucket (`private-kyc`).
- **Response**: `201 Created` `{ "documentId": "uuid", "verificationStatus": "pending" }`

---

### 2.2 Product Catalog & Search (`/categories`, `/brands`, `/products`, `/search`)

#### `GET /categories`
- **Auth**: Public | **Caching**: Redis Tagged (`cache:categories:tree`), TTL 24h | **Rate Limit**: 100 req/min
- **Response**: Full 3-level hierarchical category tree with `materializedPath`, product counts, and attribute schemas.

#### `GET /products` (PLP Search & Filter)
- **Auth**: Public | **Caching**: Redis / Meilisearch read model | **Rate Limit**: 100 req/min
- **Query Params**: `categoryId`, `brandId`, `q`, `minPrice`, `maxPrice`, `attributes[voltage]=24V`, `inStock=true`, `sort=popularity|price_asc|price_desc|newest`, `page=1`, `limit=24`.
- **Response**: Paginated product summaries with facet aggregates.

#### `GET /products/{slug}` (PDP Detail)
- **Auth**: Public | **Caching**: Redis Tagged (`cache:product:{slug}`), TTL 1h | **Rate Limit**: 100 req/min
- **Response**: Full aggregate: Product, Variants (with prices and stock status), grouped `specs`, `highlights`, `datasheetUrl`, `cadModelUrl`, Brand, Seller card, Breadcrumbs, and SEO tags.

#### `GET /search/autocomplete`
- **Auth**: Public | **Rate Limit**: 30 req/10s | **SLA**: < 50ms p95
- **Query Params**: `q=siem`
- **Response**:
  ```json
  {
    "suggestedQueries": ["siemens s7-1200", "siemens plc", "siemens vfd drive"],
    "products": [
      { "id": "uuid", "title": "Siemens SIMATIC S7-1200 CPU 1214C", "slug": "siemens-s7-1200...", "thumbnailUrl": "...", "price": { "amount": "45000.00", "currency": "BDT" } }
    ],
    "categories": [{ "id": "uuid", "name": "PLCs & Automation Controllers", "slug": "plcs" }],
    "brands": [{ "id": "uuid", "name": "Siemens", "slug": "siemens" }]
  }
  ```

---

### 2.3 Cart & Promotions (`/cart`, `/coupons`)

#### `GET /cart`
- **Auth**: Optional (Bearer token or Guest Token cookie) | **Rate Limit**: 60 req/min
- **Response**: Cart lines grouped by seller, live stock validation warnings, subtotal, shipping progress bar.

#### `POST /cart/items`
- **Auth**: Optional | **Rate Limit**: 60 req/min
- **Request**: `{ "variantId": "uuid", "quantity": 2 }`

#### `PATCH /cart/items/{variantId}`
- **Auth**: Optional | **Rate Limit**: 60 req/min
- **Request**: `{ "quantity": 4, "savedForLater": false }`
- **Response**: `200 OK` Updated line total and cart summary.

#### `DELETE /cart/items/{variantId}`
- **Auth**: Optional | **Rate Limit**: 60 req/min
- **Response**: `204 No Content`

#### `POST /cart/merge`
- **Auth**: Bearer (`customer`) | **Idempotency**: Mandatory | **Rate Limit**: 10 req/min
- **Behavior**: Merges anonymous guest Redis cart (from `guest_cart_token` cookie) into authenticated customer cart, capping quantities at available stock or max 10 units, then clears the guest cookie.
- **Response**: `200 OK` Merged cart aggregate.

#### `POST /cart/apply-coupon`
- **Auth**: Optional | **Rate Limit**: 10 req/min
- **Request**: `{ "code": "INDUSTRY40" }`
- **Response**: Calculated best promotion combination, discount breakdown, or rejection explanation.

---

### 2.4 Checkout & Orders (`/checkout`, `/orders`, `/sub-orders`)

#### `POST /checkout/reserve` (15-Minute Inventory Lock)
- **Auth**: Bearer or Guest Session | **Header**: `Idempotency-Key` (Mandatory) | **Rate Limit**: 10 req/min
- **Request**:
  ```json
  {
    "cartId": "uuid",
    "shippingAddress": { "recipientName": "Parvez", "phone": "+8801748305931", "line1": "Sha-23/E, Modhubon Tower", "area": "Gulshan", "district": "Dhaka", "division": "Dhaka", "postalCode": "1212" },
    "deliveryZone": "inside_dhaka"
  }
  ```
- **Response**: `200 OK`
  ```json
  {
    "reservationToken": "res_89a2bc1e",
    "expiresAt": "2026-10-29T02:05:00Z",
    "subtotal": { "amount": "95000.00", "currency": "BDT" },
    "discountTotal": { "amount": "5000.00", "currency": "BDT" },
    "shippingTotal": { "amount": "300.00", "currency": "BDT" },
    "taxTotal": { "amount": "4750.00", "currency": "BDT" },
    "grandTotal": { "amount": "95050.00", "currency": "BDT" },
    "sellerSplits": [
      { "sellerId": "uuid", "sellerName": "MechWare Direct", "subtotal": "55000.00" },
      { "sellerId": "uuid", "sellerName": "Siemens Authorized BD", "subtotal": "40000.00" }
    ]
  }
  ```

#### `POST /checkout/orders` (Atomic Order Placement)
- **Auth**: Bearer or Guest Session | **Header**: `Idempotency-Key` (Mandatory) | **Rate Limit**: 5 req/min
- **Request**:
  ```json
  {
    "reservationToken": "res_89a2bc1e",
    "paymentMethod": "bkash" | "stripe" | "sslcommerz" | "cod",
    "customerNotes": "Please deliver with gate pass documentation"
  }
  ```
- **Response**: `201 Created`
  ```json
  {
    "orderNumber": "SV-261029-X7K92M",
    "paymentStatus": "unpaid",
    "paymentRedirectUrl": "https://tokenized.bkash.com/checkout?paymentID=TR001...",
    "subOrders": [
      { "subOrderNumber": "SV-261029-X7K92M-01", "sellerId": "uuid", "status": "pending_payment" }
    ]
  }
  ```

#### `GET /orders/{orderNumber}` (IDOR-Protected Order Tracking & Detail)
- **Auth**: Bearer (`customer`) OR Guest Verification (`?phone=+88017...` matching `order.guest_phone` OR `?token=eyJ...` signed token) | **Rate Limit**: 30 req/min
- **Security Guard**: Unauthenticated requests without matching phone or signed token return `401 Unauthorized` (RFC 7807), eliminating enumeration/IDOR risks for B2B industrial factory addresses and downloadable vector PDF invoices.
- **Response**: Master order status, child sub-orders, live shipment courier steps, downloadable vector PDF invoice link.

---

### 2.5 Payments & Gateway Webhooks (`/payments`, `/webhooks`)

#### `POST /payments/bkash/execute` (Server-to-Server bKash Capture)
- **Auth**: Bearer or Guest Session | **Header**: `Idempotency-Key` (Mandatory) | **Rate Limit**: 10 req/min
- **Request**:
  ```json
  {
    "orderNumber": "SV-261029-X7K92M",
    "paymentId": "TR00112233AA"
  }
  ```
- **Security Behavior**: Never trusts browser redirect query parameters (`?status=success`). Backend invokes bKash Gateway `POST /tokenized/checkout/execute` server-to-server (with automatic fallback to `GET /tokenized/checkout/payment/status` on timeout), verifies `transactionStatus == "Completed"` and `amount == order.grandTotal`, then transitions Order to `'paid'` and writes balanced double-entry ledger entries.

#### `POST /webhooks/bkash/ipn` (AWS SNS Instant Payment Notification)
- **Auth**: X.509 Certificate URL validation (host must strictly match `sns.[region].amazonaws.com`) + SHA1/SHA256 signature verification.
- **Idempotency**: Automatic deduplication via Provider Transaction ID in `payments` table.
- **Behavior**: Handles asynchronous capture confirmations if customer closes browser before the redirect returns.

#### `POST /webhooks/stripe`
- **Auth**: Cryptographic Signature Header (`Stripe-Signature`) verified against Stripe webhook secret.
- **Idempotency**: Automatic deduplication via Stripe Event ID.
- **Processing**: Captures payment, confirms child `SubOrders`, dispatches `PaymentCapturedDomainEvent` to outbox. Returns `200 OK` within 500ms.

#### `POST /payments/cod/verify-otp`
- **Auth**: Bearer or Guest Session | **Rate Limit**: 5 req/min
- **Request**: `{ "orderNumber": "SV-261029-X7K92M", "otpCode": "849201" }`
- **Security Behavior**: Binds verification to `order.Id` (verifying `otp_codes.reference_id == order.Id`). Uses constant-time equality check (`CryptographicOperations.FixedTimeEquals`) against salted HMAC-SHA256 hash to prevent timing attacks. On success, transitions `orders.payment_status` to `'cod_verified'`, sets child sub-orders to `'confirmed'`, and alerts merchant dispatchers.

---

### 2.6 Courier Logistics & Tracking (`/shipping`, `/webhooks/courier`)

#### `POST /webhooks/courier/steadfast`
- **Auth**: HMAC Signature Header | **Payload**: `{ "consignment_id": 984120, "tracking_code": "ST-92819", "status": "delivered", "updated_at": "..." }`
- **Behavior**: Appends to `shipment_tracking_events`, transitions `sub_orders` to `delivered`, triggers review invitation scheduler (D+2).

---

### 2.7 Returns, Refunds & Seller Payouts (`/returns`, `/finance`)

#### `POST /returns/request`
- **Auth**: Bearer (`customer`) | **Request**:
  ```json
  {
    "orderItemId": "uuid",
    "reason": "defective_optical_sensor",
    "details": "Sensor outputs unstable 0-10V signal after 30 minutes in service.",
    "photoObjectKeys": ["uploads/rma/89a2bc1e-4567-4e89-b123-abcdef012345.webp"]
  }
  ```
- **SSRF Security Defense**: Server rejects arbitrary external URLs, strictly validating that photo keys match the regex `^uploads/(reviews|rma)/[0-9a-f\-]{36}\.(webp|jpg|png)$` residing in the internal MinIO/S3 bucket.

#### `POST /admin/refunds`
- **Auth**: Bearer (`[RequirePermission(Permissions.OrdersRefund)]`) | **Idempotency**: Mandatory
- **Request**: `{ "paymentId": "uuid", "amount": "12500.00", "reason": "Defective sensor returned" }`
- **Behavior**: Calls payment gateway refund API, writes balanced double-entry ledger entries.

#### `POST /admin/finance/payouts/execute-weekly`
- **Auth**: Bearer (`[RequirePermission(Permissions.FinancePayout)]`)
- **Behavior**: Calculates matured seller balances (`completed` sub-orders > 7 days), creates balanced ledger payout entries, generates bank/bKash transfer files.

---

### 2.8 AI Assistant & Automation Endpoints (`/ai`)

#### `POST /ai/search-parse`
- **Auth**: Public | **Rate Limit**: 20 req/min | **Payload**: `{ "query": "24V 3 phase motor drive under 30k" }`
- **Response**: `{ "category": "vfd-inverters", "attributes": { "voltage": "24V", "phase": "3" }, "maxPrice": 30000 }`

#### `GET /ai/products/{productId}/review-summary`
- **Auth**: Public | **Caching**: Redis Tagged (`cache:ai:review:{productId}`)
- **Response**: `{ "summary": "Engineers praise the high noise immunity...", "pros": ["Rugged casing", "Easy modbus setup"], "cons": ["Manual only in German/English"] }`

#### `POST /ai/assistant/chat` (Server-Sent Events / Streaming)
- **Auth**: Optional | **Rate Limit**: 10 req/min | **Stream**: Grounded text chunks with verified catalog product cards.

---

### 2.9 Wishlists, Customer Reviews & Q&A (`/wishlist`, `/reviews`, `/questions`)

#### `GET /wishlist` & `POST /wishlist/items` & `DELETE /wishlist/items/{variantId}`
- **Auth**: Bearer (`customer`) | **Rate Limit**: 60 req/min
- **Request (POST)**: `{ "productId": "uuid", "variantId": "uuid" }`
- **Response**: `200 OK` Updated wishlist items with current price & price-drop indicators.

#### `POST /reviews`
- **Auth**: Bearer (`customer`) | **Idempotency**: Mandatory
- **Request**:
  ```json
  {
    "orderItemId": "uuid",
    "rating": 5,
    "title": "Outstanding noise immunity in textile spinning plant",
    "body": "Tested under heavy inverter noise. Flawless Modbus RTU communication.",
    "photoObjectKeys": ["uploads/reviews/9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d.webp"]
  }
  ```
- **SSRF Defense**: Accepts only validated storage object keys residing in platform media buckets. Raw client HTTP/HTTPS URLs are strictly rejected.
- **Response**: `201 Created` `{ "reviewId": "uuid", "status": "pending_moderation" }`

#### `POST /reviews/{id}/helpful`
- **Auth**: Bearer (`customer`) | **Rate Limit**: 30 req/min
- **Response**: `200 OK` `{ "helpfulCount": 14 }`

#### `POST /reviews/{id}/reply`
- **Auth**: Bearer (`[RequirePermission(Permissions.CatalogUpdate)]` or matching `seller_id`)
- **Request**: `{ "replyText": "Thank you Engr. Kabir! Official technical support is always available." }`

#### `GET /products/{productId}/questions` & `POST /products/{productId}/questions`
- **Auth**: Public (GET), Bearer (POST) | **Rate Limit**: 30 req/min
- **Request (POST)**: `{ "questionText": "Does this S7-1200 CPU include analog inputs onboard?" }`

#### `POST /questions/{questionId}/answers`
- **Auth**: Bearer (Seller / Staff / Verified Customer) | **Rate Limit**: 20 req/min
- **Request**: `{ "answerText": "Yes, it includes 2 integrated 0-10V DC analog inputs." }`

#### `GET /recently-viewed` & `POST /recently-viewed`
- **Auth**: Optional (Bearer or Guest token) | **Rate Limit**: 60 req/min
- **Request (POST)**: `{ "productId": "uuid" }`

---

### 2.10 Dynamic Storefront CMS (`/cms`)

#### `GET /cms/homepage`
- **Auth**: Public | **Caching**: Redis Tagged (`cache:cms:homepage`), TTL 1h | **Rate Limit**: 100 req/min
- **Response**: Structured payload of all 12 homepage sections, scheduled active banners, deal countdown timers, and value strip copy.

#### `POST /admin/cms/banners`
- **Auth**: Bearer (`[RequirePermission(Permissions.CmsEdit)]`)
- **Request**:
  ```json
  {
    "sectionId": "uuid",
    "imageDesktopUrl": "https://s3.../hero-industry40.png",
    "imageMobileUrl": "https://s3.../hero-industry40-mobile.png",
    "headlineEn": "Empowering Bangladesh Industry with Precision Automation",
    "headlineBn": "বাংলাদেশের শিল্প কারখানা সমৃদ্ধ করতে আধুনিক অটোমেশন",
    "ctaLabelEn": "Explore Made in BD Machinery",
    "ctaUrl": "/c/made-in-bd-machinery",
    "targetDevice": "all",
    "startsAt": "2026-10-29T00:00:00Z"
  }
  ```

---

### 2.11 System Administration, Feature Flags & Health (`/admin`, `/health`)

#### `GET /admin/audit-logs`
- **Auth**: Bearer (`[RequirePermission(Permissions.AuditView)]`) | **Rate Limit**: 30 req/min
- **Query Params**: `entityName=Product`, `action=update`, `actorId=uuid`, `from=2026-10-01`, `page=1`, `pageSize=50`.
- **Response**: Paginated audit events with JSON before/after state diffs.

#### `GET /admin/feature-flags` & `PUT /admin/feature-flags/{key}`
- **Auth**: Bearer (`[RequirePermission(Permissions.SettingsEdit)]`)
- **Request (PUT)**: `{ "isEnabled": true, "rolloutPercentage": 50 }`
- **Response**: `200 OK` (Invalidates Redis flag cache tag).

#### `GET /health/live` & `GET /health/ready`
- **Auth**: Public | **Rate Limit**: Unrestricted
- **Live**: `200 OK` `{ "status": "Healthy" }`
- **Ready**: `200 OK` (Checks PostgreSQL connection, Redis ping, MinIO bucket accessibility, and Meilisearch health). If any dependent service is down, returns `503 Service Unavailable`.

---

### 2.12 Seller Center Portal (`/seller`)

#### `GET /seller/products` & `POST /seller/products` & `PUT /seller/products/{id}`
- **Auth**: Bearer (`[RequirePermission(Permissions.CatalogCreateOwn)]` + Seller Isolation Guard)
- **Moderation Rule**: If `seller.approved_listings_count < 10`, sets `product.status = 'pending_review'`; once verified with ≥10 approved items, new listings auto-publish as `'active'`.
- **Request (POST)**:
  ```json
  {
    "categoryId": "uuid",
    "titleEn": "Delta VFD-EL 2.2kW 3-Phase Inverter Drive",
    "titleBn": "ডেল্টা ভিএফডি ২.২ কিলোওয়াট থ্রি-ফেজ ইনভার্টার",
    "specs": { "Electrical": { "InputVoltage": "380V-480V", "PowerKW": "2.2" } },
    "variants": [
      { "sku": "MW-DEL-VFD-22", "price": "28500.00", "weightKg": 1.85, "initialStock": 15 }
    ]
  }
  ```

#### `POST /seller/products/bulk-import`
- **Auth**: Bearer (`[RequirePermission(Permissions.CatalogCreateOwn)]`) | **Rate Limit**: 5 req/hour
- **Request**: Multipart CSV/XLSX file with standardized technical attributes.
- **Behavior**: Asynchronous bulk validation via BackgroundService, reporting errors per row in JSON summary.

#### `POST /seller/sub-orders/{subOrderNumber}/pack`
- **Auth**: Bearer (`[RequirePermission(Permissions.OrdersFulfillOwn)]` + Seller Guard)
- **Behavior**: Transitions `sub_orders.status` from `'confirmed'` to `'processing'` then `'packed'`. Generates warehouse pick-pack list.

#### `POST /seller/sub-orders/{subOrderNumber}/ship`
- **Auth**: Bearer (`[RequirePermission(Permissions.OrdersFulfillOwn)]` + Seller Guard)
- **Request**: `{ "courierCode": "steadfast", "pickupAddressId": "uuid" }`
- **Behavior**: Integrates with courier API, creates consignment tracking number, transitions status to `'shipped'`, and returns A6 vector PDF shipping label URL.

#### `GET /seller/payouts`
- **Auth**: Bearer (`[RequirePermission(Permissions.FinanceViewOwn)]` + Seller Guard)
- **Response**: Matured ledger balance, pending escrow funds, and weekly disbursement history.

---

### 2.13 Admin Console Operations & Finance (`/admin`)

#### `POST /admin/sellers/{id}/approve` & `POST /admin/sellers/{id}/reject`
- **Auth**: Bearer (`[RequirePermission(Permissions.SellersApprove)]`)
- **Behavior**: Transitions seller status to `'approved'` or `'rejected'` with audit log entry and transactional email notification.

#### `POST /admin/products/{id}/moderate`
- **Auth**: Bearer (`[RequirePermission(Permissions.CatalogModerate)]`)
- **Request**: `{ "decision": "approve" | "reject", "rejectionReason": "Missing CE safety datasheet" }`
- **Behavior**: If approved, increments `seller.approved_listings_count` and moves product to `'active'`.

#### `POST /admin/promotions`
- **Auth**: Bearer (`[RequirePermission(Permissions.PromotionsManage)]`)
- **Request**: `{ "title": "bKash 10% Industrial Tech Week", "promoType": "percentage_discount", "discountValue": 10.0, "fundedBy": "platform", "startsAt": "...", "endsAt": "..." }`

#### `GET /admin/reconciliations`
- **Auth**: Bearer (`[RequirePermission(Permissions.FinanceReconcile)]`)
- **Query Params**: `status=flagged&settlementDate=2026-10-29`
- **Response**: Discrepancies between payment gateway deposits and internal ledger records.

---

### 2.14 B2B Turnkey Automation & Machinery RFQ (`/rfq`)

#### `POST /rfq` (Turnkey Machinery & Custom Engineering Quote Request)
- **Auth**: Public or Bearer | **Header**: `Idempotency-Key` (Mandatory) | **Rate Limit**: 5 req/min
- **Request**:
  ```json
  {
    "companyName": "Square Food & Beverage Ltd.",
    "contactName": "Engr. Parvez Mosharof",
    "phone": "+8801748305931",
    "email": "procurement@square.com.bd",
    "serviceCategory": "turnkey_automation",
    "requirementsSpec": "Automated chemical dosing skid with Siemens S7-1500 PLC, 4 peristaltic pumps, and Modbus RTU telemetry.",
    "attachmentObjectKeys": ["uploads/rfq/tender-spec-doc-001.pdf"]
  }
  ```
- **Response**: `201 Created`
  ```json
  {
    "rfqNumber": "RFQ-261029-MW904",
    "status": "submitted",
    "slaHours": 24,
    "assignedEngineeringLead": "Parvez Mosharof (Senior Systems Engineer)"
  }
  ```

#### `GET /rfq/{rfqNumber}`
- **Auth**: Bearer (`customer`) OR Contact Phone Verification (`?phone=+8801748305931`)
- **Response**: Current quote review status, official engineering estimate, downloadable formal proforma PDF invoice, and advance payment CTA.

---

### 2.15 Buyer-Seller Direct Messaging, Notifications & Disputes (`/conversations`, `/notifications`, `/disputes`)

#### `GET /conversations`
- **Auth**: Bearer (`customer` or `seller`) | **Rate Limit**: 30 req/min
- **Response**: List of active inquiry threads with unread counts, latest message preview, and merchant/buyer details.

#### `POST /conversations`
- **Auth**: Bearer (`customer`) | **Idempotency**: Mandatory | **Rate Limit**: 10 req/min
- **Request**: `{ "sellerId": "uuid", "orderId": "uuid" (optional), "subject": "Inquiry on Siemens S7-1500 lead time", "initialMessage": "Do you have 5 units in stock for immediate delivery?" }`
- **Response**: `201 Created` `{ "conversationId": "uuid", "createdAt": "..." }`

#### `GET /conversations/{id}/messages`
- **Auth**: Bearer (Thread participant) | **Rate Limit**: 60 req/min
- **Response**: Chronological messages with sender roles, text, and internal storage attachment links. Automatically marks thread messages as read.

#### `POST /conversations/{id}/messages`
- **Auth**: Bearer (Thread participant) | **Rate Limit**: 20 req/min
- **Request**: `{ "content": "Yes, all 5 units are in our Gazipur central warehouse ready for dispatch.", "photoObjectKeys": [] }`
- **Response**: `201 Created` Serialized message object.

#### `GET /notifications`
- **Auth**: Bearer | **Rate Limit**: 60 req/min
- **Query Params**: `unreadOnly=true&limit=20`
- **Response**: Array of in-app notifications with type, title, body, action URL, and read status.

#### `PATCH /notifications/{id}/read` & `POST /notifications/read-all`
- **Auth**: Bearer | **Rate Limit**: 60 req/min
- **Response**: `200 OK` Updated read status.

#### `POST /disputes`
- **Auth**: Bearer (`customer`) | **Header**: `Idempotency-Key` (Mandatory) | **Rate Limit**: 5 req/min
- **Request**: `{ "subOrderId": "uuid", "reason": "damaged_in_transit", "description": "Hydraulic coupling arrived fractured.", "photoObjectKeys": ["uploads/rma/photo-01.jpg"] }`
- **Response**: `201 Created` `{ "disputeTicketNumber": "DSP-261029-X7K", "status": "under_investigation" }`

---
