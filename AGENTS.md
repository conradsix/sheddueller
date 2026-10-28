# Repository Guidelines

## Project Structure & Module Organization

This repository is a .NET 10 task scheduling library. The solution file is `Sheddueller.slnx`.

Source lives under `src/`:

- `src/Sheddueller/` contains the core library. App-facing API types stay in the root `Sheddueller` namespace. Infrastructure is organized by folder and namespace, such as `Sheddueller.Storage`, `Sheddueller.Serialization`, `Sheddueller.Runtime`, and `Sheddueller.Enqueueing`.
- `src/Sheddueller.InMemory/` contains the in-memory store provider.

Tests live under `test/Sheddueller.Tests/`. Specs and roadmap documents live under `docs/`; image assets live in `docs/assets/`.

## Build, Test, and Development Commands

Use the solution-level commands from the repository root:

```bash
dotnet build Sheddueller.slnx --configuration Release
dotnet test --solution Sheddueller.slnx --configuration Release
dotnet format Sheddueller.slnx --verify-no-changes --verbosity minimal
```

`build` validates analyzers and warnings-as-errors in Release. `test` runs the full test suite. `format` verifies style and whitespace. VS Code tasks for build, test, and format verification are defined in `.vscode/tasks.json`.

## Coding Style & Naming Conventions

Follow `.editorconfig`. C# uses 4-space indentation, file-scoped namespaces, `using` directives inside namespaces, sorted/separated imports, braces, `var` preferences, and expression-bodied members where configured.

Prefer honest namespaces that match folders. Exceptions are DI extension methods, which intentionally use `Microsoft.Extensions.DependencyInjection` or `Microsoft.Extensions.Hosting` and suppress `IDE0130` at the top of the file. Keep app-facing API in `Sheddueller`.

## Testing Guidelines

Tests use xUnit v3, Microsoft.Testing.Platform, Shouldly, and project references to the library projects. Add or update tests for behavior changes, especially scheduler ordering, concurrency groups, serialization boundaries, worker lifecycle, and provider behavior.

Name test classes by feature, for example `WorkerTests` or `InMemoryTaskStoreTests`. Test method names use `Behaviour_Variant_Expectation`, with PascalCase inside each segment and underscores between segments. Prefer domain behavior over method names unless the method contract is the behavior, and do not suffix async test methods with `Async`.

Examples: `DelayedTask_FutureNotBefore_IsNotClaimableUntilDue`, `LeaseOwner_StaleToken_CannotCompleteTask`, `RecurringSchedule_Overdue_MaterializesOneCatchUpOccurrence`.

## Commit & Pull Request Guidelines

Commit history follows Conventional Commit-style prefixes, for example `feat:`, `fix:`, `refactor:`, `docs:`, and `chore:`. Use short imperative summaries.

Pull requests should include a concise description, the affected behavior or API surface, linked issues when relevant, and the verification commands run. Include screenshots only for documentation or future dashboard/UI work.

## Agent-Specific Instructions

Do not commit build artifacts, `bin/`, `obj/`, `TestResults/`, or `*.lscache`. Avoid public API renames unless the specs are updated at the same time.

Each package records its public API in `PublicAPI.Shipped.txt`; the build fails on any public change that isn't recorded. Add new public API to `PublicAPI.Unshipped.txt` deliberately, and treat removals or signature changes as breaking.
