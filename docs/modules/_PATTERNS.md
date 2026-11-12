# Patterns to copy (open ONE, not the whole folder)
- **Backend feature (service + upload + controller + tests):** `backend/src/Mechware.Infrastructure/Documents/ProductDocumentService.cs`, `backend/src/Mechware.Api/Controllers/AdminProductDocumentsController.cs`, `backend/tests/Mechware.IntegrationTests/Api/ProductDocumentUploadTests.cs`.
- **Read API + HTTP test:** `backend/tests/Mechware.IntegrationTests/Api/PartNumberLookupTests.cs` (DbBackedFactory, `[PostgresFact]`, JSON `data` envelope).
- **Options + DI:** `backend/src/Mechware.Infrastructure/Documents/DocumentOptions.cs` + `AddProductDocuments` in `Infrastructure/DependencyInjection.cs` (fail closed outside Development/Testing).
- **Background worker:** `OutboxRelayWorker` in `Infrastructure/Workers/CatalogBackgroundServices.cs` (PeriodicTimer, scope per batch, log-and-continue).
- **Public (anonymous) endpoint:** add to `PublicEndpoints` in `backend/tests/Mechware.IntegrationTests/Security/ApiSecurityRegressionTests.cs` with a reason; storefront reads use rate-limit policy `StorefrontRead`.
- **Frontend API module:** `frontend/packages/api-client/src/companies.ts` + `companies-model.ts` (+ its unit test).
- **Console screen:** `frontend/apps/console/src/components/CompaniesView.tsx`. **Web screen:** `frontend/apps/web/src/components/company/SitesPanel.tsx`.
- **Errors:** throw `Domain/Common/Exceptions/*` (Validation 400, Forbidden 403, Conflict 409, PayloadTooLarge 413, ServiceUnavailable 503). Never return engine/DB messages.
- **Money:** `Money` + banker's rounding once per line (ADR-0002). Integer poisha in DB.
