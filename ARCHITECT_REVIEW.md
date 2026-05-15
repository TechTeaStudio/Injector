# Architect Review — TechTeaStudio.Injector v0.2.0 + v0.3.0

Stage 3 of the orchestrator pipeline. Read-only critique of Worker A (v0.2.0 core feature wave) and Worker B (v0.3.0 MEDI sidecar) against `PLAN.md` and the conventions established in the v0.1.0 review.

## Coverage of PLAN.md — v0.2.0 (Worker A)

| Feature | Status | Evidence |
| --- | --- | --- |
| 1. Open generics | OK | `IContainerBuilder.Register(Type, Type)` / `Register(Type)`; `Registration.IsOpenGeneric`; on-the-fly closing via `Container.GetRegistrations` (double-checked locked). Covered by `OpenGenericsTests.cs` (6 tests). |
| 2. `Lazy<T>` / `Func<T>` / `Func<P, T>` | OK | `Container.TryResolveLazyOrFunc`. Covered by `LazyFuncTests.cs` (7 tests). |
| 3. `IAsyncDisposable` | OK | `IContainer : IAsyncDisposable`; `IScope : IAsyncDisposable`; `Container.DisposeAsync`; `Scope.DisposeAsync`; `DisposalHelpers.DisposeAsyncSafe`. Covered by `AsyncDisposalTests.cs` (4 tests). |
| 4. `BuildOptions { ValidateOnBuild }` | OK | `BuildOptions.cs`; `Build(BuildOptions)` overload; `Container.ValidateAllRegistrations`. Covered by `ValidateOnBuildTests.cs` (6 tests). |
| 5. `Owned<T>` | OK | `Owned.cs`; resolution branch in `Container.TryResolveLazyOrFunc`. Covered by `OwnedTests.cs` (4 tests). |
| 6. `OnActivating` / `OnActivated` | OK | `IRegistrationBuilder.OnActivating` / `OnActivated`; slow-path branch in `Container.CreateInstance`. Covered by `LifecycleHooksTests.cs` (4 tests). |
| 7. `IScope.BeginLifetimeScope` | OK | `Scope.BeginLifetimeScope`; `ChildScopeRegistry`; `Container.TryResolveFromScopeChain`. Covered by `ChildScopeTests.cs` (6 tests). |
| 8. Compiled decorators | OK | `Container.GetOrCompileDecoratorActivator` builds `Func<IResolver, object, object>` once per decorator registration. 100-iteration sanity test in `DecoratorsModulesScanTests.cs`. |
| 9. Build-time decorator validation | OK | `ContainerBuilder.ValidateDecorators`. Covered by `DecoratorsModulesScanTests.cs`. |

All nine PLAN.md features land. The 43-test v0.1 baseline now stands at 82 tests in the core project.

## Coverage of PLAN.md — v0.3.0 (Worker B)

| Surface | Status |
| --- | --- |
| `InjectorServiceProvider` | OK. `IServiceProvider`, `IServiceProviderIsService`, `IServiceProviderIsKeyedService` (added in cleanup pass), `IKeyedServiceProvider`, `IDisposable`, `IAsyncDisposable`. |
| `InjectorServiceScope` | OK. Wraps `IScope`. |
| `InjectorServiceScopeFactory` | OK. |
| `InjectorServiceProviderFactory` | OK. `(BuildOptions?)` overload added in cleanup pass for validation pass-through from host. |
| `PopulateFromServiceCollection` (all 12 descriptor variants) | OK. |
| `AsServiceProvider()` extension | OK. Non-owning by design. |
| `IKeyedServiceProvider.GetKeyedService` / `GetRequiredKeyedService` | OK. |

XML doc coverage on the sidecar is thorough; every public type and method carries `<summary>` blocks.

## Public API additions (cumulative, additive against v0.1)

Core library:

- New: `IContainerBuilder.Register(Type, Type)`, `IContainerBuilder.Register(Type)`, `IContainerBuilder.Build(BuildOptions)`.
- New: `IRegistrationBuilder.OnActivating(Action<object>)`, `IRegistrationBuilder.OnActivated(Action<object>)`.
- New: `IScope.BeginLifetimeScope(Action<IContainerBuilder>)`.
- New: `IContainer : IAsyncDisposable` (was `IDisposable` only).
- New: `IScope : IAsyncDisposable` (was `IDisposable` only).
- New: `public sealed class BuildOptions { bool ValidateOnBuild { get; init; } }`.
- New: `public sealed class Owned<T> : IDisposable, IAsyncDisposable`.
- Synthetic resolutions added (observable behaviour, no extra API surface): `Lazy<>`, `Func<>`, `Func<,...,>`, `Owned<>`, `IReadOnlyDictionary<,>`.
- New internal surface for the sidecar: `Container.IsRegisteredInternal`, `GetAllRegistrations`, `FindKeyedRegistration`, `ResolveKeyed`. Exposed via `[assembly: InternalsVisibleTo("TechTeaStudio.Injector.Extensions.DependencyInjection")]`.

No removals. The interface broadening on `IContainer` / `IScope` (adding `IAsyncDisposable`) is binary-compatible for consumers; only mock implementers would feel it.

Sidecar (all new):

- `InjectorServiceProvider`, `InjectorServiceScope`, `InjectorServiceScopeFactory`, `InjectorServiceProviderFactory`, `ScopedInjectorServiceProvider` (internal), `ResolverServiceProvider` (internal), `KeyedResolutionHelpers` (internal), `ServiceCollectionExtensions`.

## Correctness highlights

### Open generics

- Closed-generic registrations win over open-generic. Confirmed by `OpenGenericsTests.ClosedGenericRegistrationOverridesOpenGeneric`.
- Hooks and keys are copied to synthesized registrations.
- One singleton instance per closed type when an open generic is registered as singleton (`IRepo<User>` and `IRepo<Order>` get distinct instances).

### `Lazy` / `Func` / `Owned`

- `Lazy<T>` is the cache-on-`.Value` flavour (LazyThreadSafetyMode.ExecutionAndPublication).
- `Func<T>` captures the active scope at the resolve site — invoking the func later returns the scope-cached instance for scoped lifetimes.
- `Func<P, T>` selects the greediest ctor whose remaining params are resolvable. Ambiguous cases silently pick the first match (documented limitation, v0.4 follow-up).

### Async disposal

- Reverse-creation-order disposal in both `Dispose` and `DisposeAsync`.
- Sync `Dispose` falls back to `DisposeAsync().AsTask().GetAwaiter().GetResult()` for async-only-disposables. Risk of UI-thread deadlock if a consumer's `DisposeAsync` captures a `SynchronizationContext`; documented in XML doc.
- Both `Dispose` and `DisposeAsync` are idempotent.

### Validate-on-build

- Catches missing deps and circular deps; aggregates into one `AggregateException`.
- Uses a throwaway scope for scoped registrations.
- Skips open-generic registrations (closing them requires sample type args; deferred to v0.3.x).
- Singletons get materialized during validation: side effects of singleton ctors happen at `Build` rather than first `Resolve`. Documented on `BuildOptions.ValidateOnBuild`.

### Lifecycle hooks

- Slow-path triggers only when hooks are present.
- `OnActivating` runs before property/method injection; `OnActivated` after; both run before decorator wrapping (decorators are not subject to the wrapped service's hooks).
- Once-per-singleton: confirmed by `Hooks_FireOnceForSingleton`.

### Child scope

- Innermost shadows ancestors.
- Disposal isolation: parent unaffected by child dispose.
- Nested scopes work; closer parent wins.
- Decorators registered on a child builder are currently ignored (limitation, v0.4 follow-up).

### Compiled decorators

- Cached per decorator registration; 100-iteration test confirms reuse.
- Decorator with multiple service-typed ctor params: the first is bound to `inner`; documented in `RegisterDecorator` XML.

### MEDI sidecar after cleanup pass

- **Reflection removed.** `KeyedResolutionHelpers` now uses typed `internal` access exposed via `InternalsVisibleTo`. No `BindingFlags`, `GetField`, or `NonPublic` member access anywhere in the sidecar (verified by repository-wide grep).
- `IsService` reports `true` for collection wrappers (`IEnumerable<T>` / `IReadOnlyCollection<T>` / `IReadOnlyList<T>` / `ICollection<T>` / `IList<T>`) regardless of inner-type registration, matching MEDI. Also handles `Lazy<T>` / `Func<T>` / `Owned<T>` when the inner type is registered.
- `IServiceProviderIsKeyedService` implemented on both root and scoped providers.
- `InjectorServiceProviderFactory(BuildOptions?)` overload added; consumers can plumb `ValidateOnBuild` through host config.
- Disposal ownership split is intentional and tested: `container.AsServiceProvider()` is non-owning; the factory's `CreateServiceProvider` is owning. Matches `host.Dispose()` -> `IServiceProvider.Dispose()` -> entire container down.

## Thread-safety

- Singleton cache, scope cache, decorator activator cache, and open-generic synthesis cache all guarded by `_lock` with double-check on read/write. Hook invocation is outside the lock.
- Child scope locks interact with parent locks only via cache reads. No nested-lock-acquire path.
- Expression compile races: two threads may build the same lambda; the loser drops its copy. Same pattern as v0.1.

## Performance

- Compiled decorators amortize after first resolve.
- Open-generic synthesis: closed-registration cache grows unbounded if consumer requests many distinct closed types of a generic. Realistic apps are bounded; documented.
- `Func<P, T>` Expression rebuilt on every resolve (no cache). Scheduled for v0.3.1.
- `Lazy<T>` resolution uses `ConstructorInfo.Invoke`. Scheduled for v0.3.1 alongside `Func<P, T>` caching.

## Open items / backlog (v0.3.1 and v0.4+)

| Item | Severity | Status |
| --- | --- | --- |
| `IsService` ignores `IEnumerable<T>` for registered `T` | Important | Fixed in cleanup pass. |
| `IServiceProviderIsKeyedService` not implemented | Important | Fixed in cleanup pass. |
| Sidecar reflects on `Container` private fields | Critical | Fixed in cleanup pass via `InternalsVisibleTo`. |
| `InjectorServiceProviderFactory(BuildOptions?)` overload | Important | Fixed in cleanup pass. |
| `Func<P, T>` Expression recompiled every resolve | Important | Deferred to v0.3.1. |
| `Func<P, T>` ambiguous ctor selection silently picks first | Backlog | v0.4. Document; consider explicit ambiguity error. |
| Decorator with multiple inner-typed params picks first | Backlog | v0.4. |
| Open-generic registrations skipped by `ValidateOnBuild` | Backlog | v0.4. |
| Child-scope decorators ignored | Backlog | v0.4. |
| `DisposalHelpers.DisposeSync` sync-over-async deadlock risk on UI threads | Backlog | v0.4. Adds `<remarks>` on `IContainer.Dispose`. |
| Owned<T> from root container creates parentless scope | Backlog | v0.4. Document. |

## Critical count

**0 critical issues** after the cleanup pass. v0.2.0 (core) and v0.3.0 (sidecar) are both ready for NuGet.

## Verdict

**Ship v0.2.0 + v0.3.0.**

- Core library v0.2.0: every PLAN.md feature lands; 82/82 tests pass; backward compatibility preserved against v0.1.0.
- Sidecar v0.3.0: all four cleanup-pass fixes applied; 45/45 tests pass; reflection eliminated; MEDI parity strengthened (`IEnumerable<T>` in `IsService`, `IServiceProviderIsKeyedService`); host-level `BuildOptions` plumbing in place.
- Solution-wide: 127/127 tests passing in Release across `net8.0` / `net9.0` / `net10.0` (library) and `net10.0` (tests). 0 build warnings.

Outstanding follow-ups are documented and parked in the v0.3.1 / v0.4 backlog above.
