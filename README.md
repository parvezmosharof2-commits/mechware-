# MECHWARE — Industrial Commerce & Automation Platform

> **Tagline**: YOUR GATEWAY TO INDUSTRY 4.0  
> **Subtitle**: Technology for Smarter Systems | Made in Bangladesh Machinery  
> **Architecture**: C# 13 / .NET 10 Clean Architecture + Next.js 15 Monorepo + PostgreSQL 16 + Redis 7 + Meilisearch

---

## 🚀 Quickstart & One-Command Setup

### 1. Prerequisites
- **.NET SDK**: 10.0+ (`dotnet --version`)
- **Node.js**: v20+ / v24+ (`node --version`)
- **pnpm**: 9+ / 12+ (`pnpm --version`)
- **Docker & Docker Compose**: For local data services

### 2. Start Local Infrastructure (PostgreSQL, Redis, MinIO, Meilisearch, Mailpit)
```bash
docker compose -f infra/docker/docker-compose.yml up -d
```

### 3. Start C# .NET 10 Web API
```bash
cd backend
dotnet restore ShopVerse.sln
dotnet test ShopVerse.sln
dotnet run --project src/ShopVerse.Api
```
- API Swagger UI: [http://localhost:5000/swagger](http://localhost:5000/swagger)
- Liveness Probe: [http://localhost:5000/health/live](http://localhost:5000/health/live)
- Readiness Probe: [http://localhost:5000/health/ready](http://localhost:5000/health/ready)

### 3b. Load the demo catalogue (Phase 4)
```bash
cd backend
dotnet run --project src/ShopVerse.Worker -- seed-catalog \
  --media-root ../frontend/apps/web/public/seed-media --report ../docs/PHASE4_SEED_REPORT.md
dotnet run --project src/ShopVerse.Worker        # reservation-expiry + outbox workers
```
Then try `GET http://localhost:5000/api/v1/storefront/categories`.

### 4. Start Next.js 15 Frontends
```bash
cd frontend
pnpm install
pnpm dev:web      # Storefront at http://localhost:3000
pnpm dev:console  # Admin/Seller Console at http://localhost:3001
```

---

## 🏛️ Repository Architecture

```
MECHWARE/
├── backend/                             # C# 13 / .NET 10 Clean Architecture Solution
│   ├── ShopVerse.sln                    # Visual Studio 2026 / .NET 10 Solution
│   ├── src/
│   │   ├── ShopVerse.Domain/            # Pure Entities, Money Value Object, Enums, Domain Events
│   │   ├── ShopVerse.Application/       # CQRS MediatR, FluentValidation, Business Logic
│   │   ├── ShopVerse.Infrastructure/    # EF Core 10, Npgsql, Redis StackExchange, Dapper
│   │   ├── ShopVerse.Payment/           # Payment Gateway Adapters (bKash, Stripe, SSLCommerz, COD)
│   │   ├── ShopVerse.Worker/            # BackgroundService, Outbox Relay, Reservation Expiry
│   │   └── ShopVerse.Api/               # ASP.NET Core Web API, RFC 7807 ProblemDetails, Swagger
│   └── tests/
│       ├── ShopVerse.UnitTests/         # 100% Money & Startup Options Validation Tests
│       └── ShopVerse.IntegrationTests/  # WebApplicationFactory API Integration Tests
├── frontend/                            # Next.js 15 Monorepo
│   ├── apps/
│   │   ├── web/                         # Customer Storefront (App Router, ISR, next-intl)
│   │   └── console/                     # Admin & Seller Hub (App Router, TanStack Table)
│   └── packages/
│       ├── ui/                          # Cybernetic Design Tokens & Components
│       ├── config/                      # Tailwind Preset, Shared tsconfig
│       └── api-client/                  # Strongly-typed API client for .NET Web API
├── infra/
│   └── docker/                          # docker-compose.yml (Postgres 16, Redis 7, MinIO, Meilisearch)
├── docs/                                # Full Architecture, Data Model, API Contract & ADRs
├── PROGRESS.md                          # Phase-by-phase implementation tracker
└── README.md
```

---

## 🧪 Testing

### Backend Unit & Integration Tests:
```bash
dotnet test backend/ShopVerse.sln
```

### Frontend Typechecking & Builds:
```bash
cd frontend
pnpm typecheck
pnpm build
```

---

## 📄 License & Ownership
Copyright © 2024–2026 MECHWARE. All rights reserved.
Office: Sha-23/E (6th Floor), Modhubon Tower, Gulshan Link Rd, Dhaka 1212, Bangladesh.
Contact: parvez.mechware@gmail.com | +88 01748-305931 | [mechware.com.bd](https://mechware.com.bd)
