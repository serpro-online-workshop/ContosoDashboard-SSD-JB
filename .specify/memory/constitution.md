# ContosoDashboard Constitution

## Core Principles

### I. Security by Default (NON-NEGOTIABLE)
Every page and endpoint MUST require authentication (`[Authorize]`) unless explicitly public (login/logout). Authorization MUST be enforced at the service layer, not only in the UI, so users can only read or modify data they are entitled to (IDOR protection, per-user isolation). Role access follows the hierarchy Employee < TeamLead < ProjectManager < Administrator, expressed through the named policies in `Program.cs`. New features (e.g., document upload) MUST validate input (file type, size, ownership), never trust client-supplied identifiers, and keep security headers intact. Secrets MUST NOT be committed.

### II. Service-Layer Architecture
Business logic lives in `Services/` behind interfaces and is registered via dependency injection. Blazor components (`Pages/`, `Shared/`) only render and delegate; they MUST NOT access `ApplicationDbContext` directly. Entities live in `Models/`; data access goes through Entity Framework Core with migrations/seeded sample data kept in `Data/`.

### III. Offline-First with a Cloud Migration Path
The app MUST run locally with no external dependencies (local database, mock cookie authentication, local file storage). Anything that will move to Azure (Microsoft Entra ID, Blob Storage, hosted SQL) MUST sit behind an abstraction so the swap requires no changes to business logic or UI. Mock authentication is for training only and MUST be clearly labeled as such.

### IV. Test-Verified Changes
Each feature MUST define acceptance scenarios in its spec and be verified before completion. Authorization rules, IDOR protection, and data-validation logic MUST have automated tests (unit tests for services, integration tests for auth flows) once a test project exists; until then, manual verification steps MUST be documented in the feature's quickstart. Bugs fixed MUST be accompanied by a regression check.

### V. Spec-Driven Simplicity
Work follows spec → plan → tasks → implementation. Implement what the spec requires (YAGNI); prefer built-in ASP.NET Core / Blazor / EF Core capabilities over new dependencies. Any added project, library, or abstraction MUST be justified in the plan's Complexity Tracking table.

## Technology & Quality Constraints

- **Stack**: .NET 10 (`net10.0`), ASP.NET Core Blazor Server, Entity Framework Core with SQLite (per `ContosoDashboard.csproj`), cookie authentication with claims-based identity, Microsoft.Identity.Web / OpenID Connect reserved for the Entra ID path. Nullable reference types and implicit usings stay enabled.
- **Data**: Schema changes via EF Core migrations; no destructive changes without a migration plan. Business-sensitive data MUST NOT be logged.
- **UX & Accessibility**: UI targets WCAG 2.1 AA; layouts MUST work responsively and show clear loading, empty, and error states.
- **Performance**: Dashboard and list pages SHOULD respond within ~2 seconds under typical training-scale load (hundreds of users); use async I/O for all database and file operations.
- **Error handling**: Services return meaningful errors; failures are logged with `ILogger` and never expose stack traces or internal details to users.
- **Conventions**: C# standard naming, `async`/`await` with `Async` suffix, one class per file, files organized per the existing folder layout.

## Development Workflow & Quality Gates

- Features begin with `/speckit.specify`, are planned with `/speckit.plan`, and broken down with `/speckit.tasks`; the plan's Constitution Check MUST pass before research and be re-checked after design.
- Before a change is considered done: the solution builds without warnings introduced by the change, acceptance scenarios pass, and no authorization or validation rule is weakened.
- Reviews MUST verify compliance with Principles I–V; deviations require written justification.
- Keep changes small and focused; do not mix unrelated refactoring with feature work.

## Governance

This constitution supersedes other practices for this repository. Amendments require a documented rationale, an updated version number, and a check that `.specify/templates/` (plan, spec, tasks) remain consistent with it. Versioning follows semantic rules: MAJOR for removed or redefined principles, MINOR for added principles or materially expanded guidance, PATCH for clarifications. All specs, plans, and reviews MUST verify compliance; unavoidable complexity MUST be justified in the plan.

**Version**: 1.0.0 | **Ratified**: 2026-10-20 | **Last Amended**: 2026-10-20
