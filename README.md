# Immersive Composition

`com.immersive.composition` is an independent, engine-free keyed composition
kernel. It reconciles owner contributions into materialized parts without
knowing Unity objects or Immersive Framework lifecycle concepts.

The next distribution candidate is **0.1.0-preview.3**; signed OpenUPM
publication is pending. Every
consumer-facing contract is **Experimental** under the Framework API maturity
governance: signatures and behavior may change before a Stable baseline. The
package contains no serialized Unity schema.

## Installation

Framework Actor composition uses this package through
`com.immersive.framework`'s declared UPM dependency. In a consumer project,
install Framework from the `com.immersive` OpenUPM scope and let Unity resolve
Composition transitively. Do not add a parallel local path or Git dependency
for Composition beside Framework.

Direct installation is intended only for packages or tools that integrate the
generic kernel without using Framework Actor surfaces. The package is
Experimental; see the Framework's IF-ADR-050 documentation for Actor lifecycle
and integration contracts.

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
