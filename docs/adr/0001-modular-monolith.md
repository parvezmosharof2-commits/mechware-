# ADR 0001: Modular Monolith with Clean Architecture (.NET 10 + Next.js 15)

## Status
Accepted

## Date
2026-10-29

## Context
MECHWARE is an enterprise-grade digital commerce and engineering platform for industrial machinery, factory automation components, and turnkey engineering services. The system requires:
- High transaction integrity for financial ledgers, inventory deductions, and multi-vendor order routing.
- Sub-50ms read latency for industrial product specifications and catalog navigation.
- Strict bounded contexts (Identity, Catalog, Inventory, Cart, Checkout, Order, Payment, Shipping, Returns, Seller, Finance, CMS, Search, AI, Audit).
- Support for rapid development without the operational complexity and distributed failure modes of microservices (e.g. distributed transactions, network latency between services, complex orchestration).

## Decision
We adopt a **Modular Monolith architecture** for the backend built on **C# 13 / .NET 10 ASP.NET Core** paired with a **Next.js 15 Monorepo** for the web tier.

### Architectural Rules:
1. **Bounded Contexts**: Each domain module (Catalog, Order, Payment, etc.) encapsulates its domain models, business logic, and database entities.
2. **Explicit Interfaces**: Modules communicate strictly through public Application service interfaces (`IOrderService`, `IInventoryService`) or via asynchronous Domain Events. Direct cross-module repository access or EF Core `Include` across module boundaries is prohibited.
3. **Single Database with Logical Schemas**: A single PostgreSQL 16 database is used, with tables logically grouped by domain prefix or PostgreSQL schemas (`identity.*`, `catalog.*`, `order.*`, `finance.*`).
4. **Clean Architecture in C#**:
   - `ShopVerse.Domain`: Pure domain entities, value objects (e.g. `Money`), domain events, enums. Zero external dependencies.
   - `ShopVerse.Application`: CQRS Commands and Queries (MediatR), validation pipelines (FluentValidation), event handlers, application ports/interfaces.
   - `ShopVerse.Infrastructure`: EF Core DbContext, Npgsql, Redis caching, MinIO/S3, Mailpit/SMTP, Meilisearch client.
   - `ShopVerse.Payment`: Gateway adapters (Stripe, bKash, SSLCommerz, COD).
   - `ShopVerse.Worker`: Background services (Hangfire / .NET BackgroundService) for outbox processing, reservation expiry, and reconciliations.
   - `ShopVerse.Api`: ASP.NET Core Web API, controllers, middleware, RFC 7807 problem details, OpenAPI spec.
5. **Next.js 15 Frontend Monorepo**:
   - `apps/web`: Public storefront with App Router (RSC, ISR, SSR).
   - `apps/console`: Protected Admin and Seller portals (client-side data tables, TanStack Table).
   - `packages/ui`: Design system, token-driven Tailwind components.
   - `packages/api-client`: Auto-generated typed TypeScript client from .NET OpenAPI.

## Consequences
### Positive:
- **ACID Transactions**: Local database transactions guarantee absolute consistency for order splitting, stock reservation, and ledger balancing.
- **Simplified Deployment & Operations**: Single deployable container for the backend API and worker, reducing DevOps overhead.
- **Type Safety & Refactoring**: C# 13 and TypeScript ensure compile-time verification across the entire stack.
- **Future Extraction**: If a specific module (e.g., Search or Notification) needs independent scaling, its clean boundary allows extraction into a standalone microservice without rewriting domain logic.

### Negative / Trade-offs:
- Must enforce architectural boundaries via tooling (e.g., architecture tests or Roslyn analyzers) to prevent developers from bypassing module interfaces.
- Deployment updates the entire backend service, requiring zero-downtime rolling deployments.
