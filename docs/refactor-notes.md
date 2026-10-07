# Refactor pass

This pass preserves the five packages' public API and existing behavior. The changes are grouped into shared enqueueing/testing policy, shared startup and retention policy, worker lifecycle/invocation, PostgreSQL operation responsibilities, dashboard presentation/state, and test organization.

## Compatibility checks

`PublicApiSnapshotTests` compares the exported API of every package, including Razor-generated components, with checked-in text manifests. It covers public/protected member signatures, operators, extension-method metadata, parameter names/defaults, generic constraints, nullability, constants, and enum values. The manifests were generated from an independent build of the original revision; normal test runs never update them.

For an intentional API change, regenerate with `SHEDDUELLER_UPDATE_API_BASELINE=1 dotnet test --solution Sheddueller.slnx --configuration Release`, then review every manifest diff. Do not regenerate snapshots to make a refactor pass.

Dashboard styles are ordinary static CSS, loaded at the same layout/page positions as the former inline styles. CSS isolation is not used because it would change selector reach into child components. Existing selectors, ordering, browser-storage keys, and JS interop names are retained.

## Verification

- Release build and format verification pass without warnings or changes.
- All 421 tests pass with PostgreSQL 14, 16, and 18, including API snapshots, provider contracts, retention, worker cancellation/scope disposal, stale responses, and mounted dashboard asset delivery.
- All 10 extracted stylesheets retain their original CSS rules; the 41 multiline SQL/JSON literals in the split provider operations retain their text apart from helper-name qualifications.
- Local browser checks passed for live updates, pagination, column resizing by keyboard, saved views/default restoration after reload, Escape focus restoration, filtering, job cancellation, invocation display, and stylesheet delivery. The temporary sample host and database were removed afterward.

## Separate follow-up

The jobs page already leaves the load-more flag set when filters change while a pagination request is pending: the old request's `finally` only clears the flag when its query version is current. The extracted list controller preserves this behavior. Correcting pagination recovery requires a separate behavior fix and regression test.

The core enqueuer checks dependency cycles before graph idempotency, while the testing fake checks idempotency first. Characterization tests preserve the resulting exception precedence rather than silently changing it during code sharing.
