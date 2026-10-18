# Immersive Composition

`com.immersive.composition` is an independent, engine-free keyed composition
kernel. It reconciles owner contributions into materialized parts without
knowing Unity objects or Immersive Framework lifecycle concepts.

**0.1.0-preview.3** has a signed [GitHub Release](https://github.com/ImmersiveGames/com.immersive.composition/releases/tag/v0.1.0-preview.3).
Its OpenUPM registry listing and installability were not verified in the latest
documentation check. Every consumer-facing contract is **Experimental** under
the Framework API maturity governance: signatures and behavior may change
before a Stable baseline. The package contains no serialized Unity schema.

## Installation

For a released Framework installed from OpenUPM, install Framework from the
`com.immersive` scope and let Unity resolve Composition transitively from the
Framework package dependency. Do not add a second Composition source in this
registry-based setup. Confirm that the required Framework and Composition
versions are visible in OpenUPM before choosing this route.

For Framework development from Git, Unity does not resolve a custom package's
SemVer dependency to another Git package. Declare Composition as a direct
project dependency alongside Framework (and the Framework's other custom Git
dependencies). A Git URL without a ref selects the repository's default
branch, while `Packages/packages-lock.json` records the resolved commit; Unity
does not follow later branch commits until Package Manager updates the package.

When consuming Framework from Git, Composition must also be declared directly
in the project manifest to satisfy Unity's Git dependency resolution. This
does not create a parallel Actor runtime: use Framework Actor surfaces for
Framework Actors. Direct use of the Composition API is intended for independent
packages or tools integrating the generic kernel. The package is Experimental;
see the Framework's IF-ADR-050 documentation for Actor lifecycle and
integration contracts.

## Core flow

Create one composer for a content root, open an owner contribution, then set or
remove typed content keys. Each part definition supplies an opaque descriptor
and a materializer. The Composer prepares candidates asynchronously, applies
and detaches them synchronously at a serialized commit boundary, increments its
revision for applied effects, publishes ordered changed-key deltas, then asks
materializers to release replaced resources.

```csharp
var composer = new CompositionComposer();
var baseVisual = composer.CreateContribution(CompositionOwnerId.Create("actor.profile"), 0);

CompositionResult result = await baseVisual.SetPartAsync(
    new PartDefinition(ContentKey.Create("visual"), visualDescriptor, visualMaterializer));
```

Contribution owners own desired intent. They do not automatically own the
resources or root represented by a part. Materializers define how acquired and
adopted resources are applied, detached, and released.

Call `SetPartWhileCurrentAsync`, `SetPartsWhileCurrentAsync`, or
`RemovePartWhileCurrentAsync` when an operation is bound to an external target
such as a physical Actor occurrence. The Composer checks the initiating target
and guarded winners in the affected dependency closure after preparation and
throughout the serialized effect phase. A stale contribution is invalidated
when observed, and staged work is released without being applied. `CloseAsync`
remains available for cleanup after the external target is no longer current.

## Boundaries

- Runtime assembly: `Immersive.Composition.Runtime`, with `noEngineReferences: true`.
- Test assembly: focused EditMode tests reference only this runtime assembly and Unity Test Framework.
- No dependency on `com.immersive.framework`, Actor/Player types, UnityEngine, Foundation, Logging, Pooling, Addressables, or global services.
- Unity-facing materializers and Framework integration belong at consumer boundaries and are not included in this kernel.

See [Composition Boundary](Documentation~/Composition-Boundary.md) for behavior,
ownership, maturity, and validation limits.

## License

Licensed under the [MIT License](LICENSE.md).
