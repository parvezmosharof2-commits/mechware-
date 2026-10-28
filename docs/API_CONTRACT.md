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
- **Response**: `200 OK` with rotated access token & updated refresh cookie. `401 Unauthorized` (or `403 Token Family Revoked` upon reuse).

#### `POST /auth/otp/send` & `POST /auth/otp/verify`
- **Auth**: Public | **Rate Limit**: 3 req/min | **Payload**: `{ "phone": "+88017...", "type": "cod_confirm" | "login" }`

#### `GET /users/me/addresses` & `POST /users/me/addresses`
- **Auth**: Bearer (`customer`) | **Rate Limit**: 60 req/min
- **Address Request**:
  ```json
  { "label": "Factory HQ", "recipientName": "Engr. Kabir", "phone": "+8801711000000", "line1": "Plot 12, Export Processing Zone", "line2": "Building B", "area": "Savar", "district": "Dhaka", "division": "Dhaka", "postalCode": "1349", "isDefaultShipping": true }
  ```

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

#### `GET /orders/{orderNumber}` (Public Tracking & Account Detail)
- **Auth**: Bearer (or phone verification for guest) | **Rate Limit**: 30 req/min
- **Response**: Master order status, child sub-orders, live shipment courier steps, downloadable vector PDF invoice link.

---

### 2.5 Payments & Gateway Webhooks (`/payments`, `/webhooks`)

#### `POST /payments/bkash/callback` & `POST /webhooks/stripe`
- **Auth**: Cryptographic Signature Header (`X-Signature` / `Stripe-Signature`)
- **Idempotency**: Automatic deduplication via Provider Transaction ID in `payments` table.
- **Processing**: Captures payment, confirms child `SubOrders`, dispatches `PaymentCapturedDomainEvent` to outbox. Returns `200 OK` within 500ms.

#### `POST /payments/cod/verify-otp`
- **Auth**: Bearer or Guest Session | **Request**: `{ "orderNumber": "SV-261029-X7K92M", "otpCode": "849201" }`
- **Response**: Confirms COD order, marks sub-orders as `confirmed`, alerts merchant dispatchers.

---

### 2.6 Courier Logistics & Tracking (`/shipping`, `/webhooks/courier`)

#### `POST /webhooks/courier/steadfast`
- **Auth**: HMAC Signature Header | **Payload**: `{ "consignment_id": 984120, "tracking_code": "ST-92819", "status": "delivered", "updated_at": "..." }`
- **Behavior**: Appends to `shipment_tracking_events`, transitions `sub_orders` to `delivered`, triggers review invitation scheduler (D+2).

---

### 2.7 Returns, Refunds & Seller Payouts (`/returns`, `/finance`)

#### `POST /returns/request`
- **Auth**: Bearer (`customer`) | **Request**: `{ "orderItemId": "uuid", "reason": "damaged_pins", "photos": ["https://s3.../img1.jpg"] }`

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
