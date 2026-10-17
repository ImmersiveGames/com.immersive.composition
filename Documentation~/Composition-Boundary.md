# Immersive Composition boundary

## Authority and maturity

This package implements the generic composition-kernel boundary selected by IF-ADR-050 in `com.immersive.framework` and scoped by D03 in the Framework plan. IF-ADR-050 remains the architecture authority; this package does not own Actor, Player, Session, Route, Activity, physical roots, Actor identity, or Framework `RuntimeContentOwner`.

The package's initial public consumer contracts are **Experimental**. The pre-release version and this declaration do not promise compatibility with a Stable baseline. No Unity asset or serialized schema is introduced. Maturity is documented here rather than importing the Framework's type attribute, which would violate the package's dependency boundary.

## Responsibilities

| Contract | Responsibility | Boundary |
|---|---|---|
| `ContentKey`, `PartDefinition` | Identify an opaque part slot and declare the descriptor, materializer, and required keys. | Keys are ordinal and case-sensitive. Empty values, surrounding whitespace, whitespace, and control characters are rejected. The kernel does not interpret key naming conventions. |
| `CompositionOwnerId`, `CompositionContribution` | Scope desired entries to a caller-owned contribution and priority. | A contribution owns its intent, not necessarily the resource or root. An owner ID is unique for the lifetime of one Composer, including after close. |
| `CompositionComposer` | Resolve winning owners, validate the desired dependency graph, serialize synchronous apply/detach effects, track committed parts and revisions. | One Composer represents one consumer-selected content context. The producer retains root lifecycle authority. |
| `IPartMaterializer`, result types | Prepare, apply, detach, and release a part through an explicit provider. | Preparation is asynchronous; apply and detach report whether effects may have occurred; release reports released, retained, pending, or failed. The provider implements the meaning of `Acquired` and `Adopted`. |
| `CompositionResult`, `CompositionDelta` | Report operation outcome and publish changed keys after a revision is committed. | Revision is monotonic within one Composer. Partial post-effect failures are reported as degraded with `EffectsMayHaveApplied`; the kernel does not claim to roll back external effects. |

## Resolution and commit behavior

- For a key with multiple contributions, the unique highest-priority contribution wins. A tie at the highest priority is rejected before materializer effects.
- Missing dependencies and dependency cycles are rejected before preparation. Updating a dependency includes affected dependents in the operation closure; prerequisites are prepared and applied before dependents, and detached in reverse dependency order.
- Unrelated operations may prepare and commit while another operation waits in asynchronous preparation or release. A short global gate serializes synchronous application effects; keyed gates serialize overlapping dependency closures.
- A later operation invalidates earlier work for the exact overlapping key closure. If a stale preparation eventually returns an acquired resource, that attempt releases it and cannot publish content.
- Successful content effects advance one revision and enqueue one ordered delta. Delta handlers are read-only observations; handler exceptions are returned as notification issues and do not undo a committed effect.
- Replaced resources are released after commit notification and outside the global application gate. A materializer must scope release to the exact supplied materialization; release of an old instance must not mutate the replacement.

## Resource ownership

`PartMaterialization.Ownership` communicates acquisition versus adoption to the materializer. The kernel always asks the corresponding materializer to release its handle after detach or abandoned preparation. For `Adopted`, that provider must retain the externally owned resource rather than destroying it. For `Acquired`, the provider releases only what it acquired. `Provenance` is opaque to the kernel and is intended to preserve exact adoption evidence at an integration boundary.

`PartReleaseOutcome.Pending` and `Failed` are explicit non-success outcomes. Scheduling Unity `Destroy` is not proof of physical destruction; Unity-specific providers must report only the completion level their own contract can establish.

## Framework integration boundary

The intended dependency direction is:

```text
com.immersive.framework
        ↓
com.immersive.composition
```

Framework Core remains the authority for Actor/Player identities, ActorProfile selection, root activation/replacement/release, and lifecycle ownership. The Framework integration will translate a profile into the Base `visual` contribution and use its existing `VisualContentMount`; it must not maintain a second `presentationPrefab` materialization path. Scene-provided visuals are adopted with exact provenance. None of that integration or a Unity materializer is part of this D03 package.

No repository remote, published package, or Framework manifest dependency is configured in this local candidate. D04 must pin a concrete D03 candidate only after this package is imported, compiled, and its focused tests are run in Unity.

## Validation

The EditMode test sources cover precedence, equal-priority conflicts, dependency rejection/order, independent-key concurrency, stale completion, close invalidation, non-owner removal, adopted-resource retention, partial apply failure, and ordered deltas across delayed release. They are isolated from Framework assemblies. This source checkout has not yet been imported or compiled by Unity; manual Unity import/compile and EditMode execution remain required before D03 acceptance.
