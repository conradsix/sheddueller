# Repository Guidelines

## Project Structure & Module Organization

This repository is a .NET 10 job scheduling library. The solution file is `Sheddueller.slnx`.

Source lives under `src/`:

- `src/Sheddueller/` is the core package: the app-facing API (enqueueing, recurring schedules, job and concurrency group management, inspection readers) and the store contracts providers implement. App-facing API types stay in the root `Sheddueller` namespace. Infrastructure is organized by folder and namespace, such as `Sheddueller.Storage`, `Sheddueller.Inspection.*`, `Sheddueller.Runtime`, `Sheddueller.Serialization`, `Sheddueller.Enqueueing`, and `Sheddueller.Scheduling`.
- `src/Sheddueller.Worker/` runs jobs: the hosted worker that claims and executes them, lease heartbeats, job log capture, and job retention.
- `src/Sheddueller.Postgres/` is the PostgreSQL store provider, with schema migrations and LISTEN/NOTIFY wake-ups.
- `src/Sheddueller.Testing/` contains test doubles for application code, such as `FakeJobEnqueuer` and `FakeRecurringScheduleManager`.
- `src/Sheddueller.Dashboard/` is the embedded Blazor Server dashboard, a Razor class library mapped with `MapShedduellerDashboard`.
- `src/Shared/` is linked source, not a project. `Shared/Hosting` (hosted service registration, startup validation, the job retention loop) is compiled into both the worker and dashboard packages so either can run them alone; markers in the core package keep a host that adds both from running them twice.

Tests live under `test/`:

- `Sheddueller.Tests` covers the core package: registration, enqueueing, the managers, invocation formatting, and package boundaries.
- `Sheddueller.Worker.Tests` covers the worker against in-memory store stubs: reliability and shutdown, rate limit timing, progress, job logging, retention, and registration.
- `Sheddueller.Postgres.Tests` runs the provider against a real PostgreSQL started with Testcontainers, so it needs Docker.
- `Sheddueller.Testing.Tests` covers the test doubles, including validation parity with the real enqueuer and schedule manager.
- `Sheddueller.Dashboard.Tests` covers the dashboard's pure helpers and endpoint registration. Shared builders live in `TestData`.
- `Sheddueller.ProviderContracts` is linked source, not a project: abstract contract suites for job stores, inspection, and retention that each provider test project subclasses.

`samples/Sheddueller.SampleHost` hosts the dashboard, a worker, and a launcher page for creating demo jobs and schedules. Image assets live in `assets/`. `docs/` is currently empty.

## Build, Test, and Development Commands

Use the solution-level commands from the repository root:

```bash
dotnet build Sheddueller.slnx --configuration Release
dotnet test --solution Sheddueller.slnx --configuration Release
dotnet format Sheddueller.slnx --verify-no-changes --verbosity minimal
```

`build` validates analyzers and warnings-as-errors in Release. `test` runs the full test suite, including the Postgres tests, which need Docker. `format` verifies style and whitespace. CI runs the same three commands. VS Code tasks for build, test, and format verification are defined in `.vscode/tasks.json`.

To run the sample, start PostgreSQL from `docker-compose.yml`, then run the host; it applies migrations on startup:

```bash
docker compose up -d postgres
dotnet run --project samples/Sheddueller.SampleHost --urls http://localhost:5000
```

The launcher is at `http://localhost:5000/` and the dashboard at `http://localhost:5000/sheddueller`.

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces, `using` directives inside namespaces, sorted/separated imports, braces, `this.` qualification, `var` preferences, and expression-bodied members where configured.

Prefer honest namespaces that match folders. Exceptions are DI extension methods, which intentionally use `Microsoft.Extensions.DependencyInjection` or `Microsoft.Extensions.Hosting` and suppress `IDE0130` at the top of the file. Keep app-facing API in `Sheddueller`.

In the dashboard, pages live in `Components/Pages`, UI primitives in `Components/Ui`, and pure helpers in `Internal/`, where they are unit tested. Styles use CSS cascade layers in `wwwroot/css` (`sheddueller.css` declares the layers; per-page sheets in `css/pages` sit in the `pages` layer). Scripts live in `wwwroot/js` and attach everything .NET calls to the `sheddueller` global (for example `sheddueller.throughputChart.render`); `wwwroot/vendor` holds third-party assets.

## Testing Guidelines

Tests use xUnit v3, Microsoft.Testing.Platform, Shouldly, and project references to the library projects. Add or update tests for behavior changes, especially scheduler ordering, concurrency groups, serialization boundaries, worker lifecycle, and provider behavior. Behavior every provider must share belongs in `Sheddueller.ProviderContracts`. Test behavior and logic, not markup: don't add dashboard tests that only check an element, class, or piece of copy exists.

Name test classes by feature, for example `WorkerReliabilityTests` or `PostgresMigrationTests`. Test method names use `Behaviour_Variant_Expectation`, with PascalCase inside each segment and underscores between segments. Prefer domain behavior over method names unless the method contract is the behavior, and do not suffix async test methods with `Async`.

Examples: `DelayedJob_FutureNotBefore_IsNotClaimableUntilDue`, `LeaseOwner_StaleToken_CannotMutateReclaimedJob`, `RecurringSchedule_DueOccurrence_MaterializesClaimableJob`.

## Commit & Pull Request Guidelines

Commit history follows Conventional Commit-style prefixes with an optional scope, for example `feat:`, `fix(postgres):`, `refactor(dashboard):`, `test:`, `docs:`, and `chore:`. Use short imperative summaries.

Pull requests should include a concise description, the affected behavior or API surface, linked issues when relevant, and the verification commands run. Include screenshots only for dashboard UI or documentation changes.

## Agent-Specific Instructions

Do not commit build artifacts, `bin/`, `obj/`, `TestResults/`, or `*.lscache`.

Each package records its public API in `PublicAPI.Shipped.txt`; the build fails on any public change that isn't recorded. Add new public API to `PublicAPI.Unshipped.txt` deliberately, and treat removals or signature changes as breaking.
