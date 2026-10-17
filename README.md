# Immersive Composition

`com.immersive.composition` is an independent, engine-free keyed composition kernel. It reconciles owner contributions into materialized parts without knowing Unity objects or Immersive Framework lifecycle concepts.

The package is at **0.1.0-preview.1**. Every consumer-facing contract is **Experimental** under the Framework API maturity governance: signatures and behavior may change before a Stable baseline. The package contains no serialized Unity schema.

## Core flow

Create one composer for a content root, open an owner contribution, then set or remove typed content keys. Each part definition supplies an opaque descriptor and a materializer. The Composer prepares candidates asynchronously, applies and detaches them synchronously at a serialized commit boundary, increments its revision for applied effects, publishes ordered changed-key deltas, then asks materializers to release replaced resources.

```csharp
var composer = new CompositionComposer();
var baseVisual = composer.CreateContribution(CompositionOwnerId.Create("actor.profile"), 0);

CompositionResult result = await baseVisual.SetPartAsync(
    new PartDefinition(ContentKey.Create("visual"), visualDescriptor, visualMaterializer));
```

Contribution owners own desired intent. They do not automatically own the resources or root represented by a part. Materializers define how acquired and adopted resources are applied, detached, and released.

## Boundaries

- Runtime assembly: `Immersive.Composition.Runtime`, with `noEngineReferences: true`.
- Test assembly: focused EditMode tests reference only this runtime assembly and Unity Test Framework.
- No dependency on `com.immersive.framework`, Actor/Player types, UnityEngine, Foundation, Logging, Pooling, Addressables, or global services.
- Unity-facing materializers and Framework integration belong at consumer boundaries and are not included in this kernel.

See [Composition Boundary](Documentation~/Composition-Boundary.md) for behavior, ownership, maturity, and validation limits.
