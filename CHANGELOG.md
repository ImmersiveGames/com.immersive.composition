# Changelog

All notable changes to this package are documented here.

## [0.1.0-preview.3] - 2026-10-18 — OpenUPM candidate; unpublished

- Prepares the existing Experimental composition kernel for signed GitHub Release and OpenUPM distribution.
- No Runtime or public API changes from `0.1.0-preview.2`.
- Framework consumes this package transitively; Framework Actor consumers should not add a second direct Composition source.

## [0.1.0-preview.2] - 2026-10-18 — local Experimental candidate; unpublished

- Adds owner-scoped external-target guards, including invalidation of stale guarded winners in affected dependency closures, late acquisition cleanup and cancellation during preparation.
- Corrects issue accumulation for read-only `IList<CompositionIssue>` results and adds focused EditMode regressions. The Project Owner reports this local source in the QAFramework 430/430 EditMode run, with Composition 16/16; import of the immutable local tarball remains pending.
- No tag, registry publication or Framework dependency on Framework-owned contracts is introduced.

## [0.1.0-preview.1] - 2026-10-17

- Introduced the Experimental engine-independent keyed composition kernel.
- Added owner-scoped contributions, priority resolution, dependency validation, asynchronous preparation, commit results, deltas, and explicit resource ownership outcomes.
- Added isolated EditMode test sources. Unity import and test execution are pending manual validation.
