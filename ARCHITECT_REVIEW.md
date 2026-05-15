# Architect Review — TechTeaStudio.Injector v0.1.0

Stage 3 of the orchestrator pipeline. Read-only critique against the issue body's bullets and the codebase conventions used by `33-hh-story-generator` (the pilot) and `WordDataBase` / `ConfigBase`.

## Coverage of the issue body

| Issue bullet | Status |
| --- | --- |
| Lightweight DI container for .NET 9.0+ | OK — multi-targets `net8.0;net9.0;net10.0`. |
| Fluent registration API | OK — `ContainerBuilder` / `IRegistrationBuilder`. |
| Transient / scoped / singleton lifetimes | OK — `AsTransient` / `AsScoped` / `AsSingleton`. |
| Constructor injection | OK — greediest resolvable ctor with parameterless fallback. |
| Property injection | OK — `[Inject]` on settable property. |
| Method injection | OK — `[Inject]` on a public method, parameters resolved. |
| Circular dependency detection | OK — runtime-side ambient stack catches cycles that span the compiled-activator boundary. `CircularDependencyException` carries the full chain. |
| Module / assembly bulk registration | OK — `IModule` + `RegisterAssembly(asm, filter, lifetime)`. |
| Decorator pattern | OK — `RegisterDecorator<TService, TDecorator>()`; chain order is innermost = first registered. |
| Expression-tree compilation for perf | OK — `Activators.BuildActivatorExpression` builds a `Block` with `New(ctor, ...)` + property/method injection, compiles to `Func<IResolver, object>`, cached per registration. |
| XML docs on every public type | OK — `GenerateDocumentationFile` is enabled on the library `.csproj`. |
| Tests | OK — 43 xUnit tests across four files. |
| Examples | OK — `samples/TechTeaStudio.Injector.Sample` runs end-to-end. |
| Resolve `IEnumerable<T>` | OK — auto-handled by `TryResolveCollection`. Also covers `IReadOnlyList<T>` / `IReadOnlyCollection<T>`. |
| Resolve `IDictionary<TKey, T>` | OK — keyed via `WithKey(...)` + `ResolveDictionary<TKey, T>()`. Auto-handled when injected as `IDictionary<TKey, T>` / `IReadOnlyDictionary<TKey, T>`. |

Verdict: every bullet in the issue body is covered.

## Thread-safety

- `_lock` (a private `object`) guards the three mutable internal maps: `_activatorCache`, `_singletons`, and `_ownedDisposables`.
- `_byServiceType` is built once in the constructor and treated as read-only afterwards. No lock needed.
- Cycle detection uses `[ThreadStatic]` `Stack<Type>` + `HashSet<Type>`. Each resolving thread sees its own state, so there is no contention.
- **One small race:** in the singleton branch, two threads can both build an instance, then one loses to the other on the cache add. The loser correctly disposes its duplicate — but if that duplicate's *dependencies* themselves had side effects (registered owned disposables, etc.), those side effects are not undone. For v0.1 this is documented behaviour; revisit if it bites a real consumer.

## Performance — expression-tree compilation

- One compilation per registration, cached forever inside the container's lifetime.
- The compiled lambda is `Func<IResolver, object>`. The body is a `Block`:
  - `new T(r.Resolve(typeof(P1)), r.Resolve(typeof(P2)), ...)` (with `Expression.Convert` to the parameter type)
  - then optional `inst.Prop = (PropType)r.Resolve(typeof(PropType));` lines
  - then optional `inst.MethodInject((P1)r.Resolve(...), ...)` lines
  - then `(object)inst` as the block result.
- Hot path: a single virtual call to `IResolver.Resolve` per dependency, then a delegate invoke for the ctor. About as fast as one can get without source generators.
- Decorators are still built via reflection-based `ctor.Invoke(args)`. That is slow per call but only matters if you decorate a hot-loop service. Future optimisation: compile decorator stacking too.

## Disposal — idempotent?

- `Container.Dispose`: guarded by `if (_disposed) return;` and a hot-path `_disposed = true`. Subsequent calls are no-ops. ✓
- `Scope.Dispose`: guarded by a `_lock`-held `_disposed` flag — first call snapshots and disposes; second call returns immediately. ✓
- Per-instance `Dispose()` calls are wrapped in `try { … } catch { /* swallow */ }`; this prevents one badly-behaved service from blocking the rest of the cleanup. Trade-off: we hide exceptions; this matches MS DI's practice but is worth a note in the README.

## Open items (non-blocking)

1. **Root-tracked transient disposables grow without bound** if a long-running app resolves transient `IDisposable`s from the root container in a loop. Most consumers should resolve transients from a scope; the README's "Lifetimes at a glance" section covers this. *Not a fix for v0.1.*
2. **Decorator activation via `ctor.Invoke`** — slower than compiled lambdas. Acceptable for v0.1 because decorators are rarely on truly hot paths. *Schedule for v0.2.*
3. **`RegisterDecorator` doesn't validate** that the wrapped service has at least one registration. The error surfaces only at `Resolve`. A `Build()`-time check would be friendlier. *Nice-to-have.*
4. **`As(params Type[])` aliases** create a factory-registration that delegates to the primary service type. That preserves the underlying lifetime cache correctly (the alias itself is transient, but the inner resolve hits the cache). Verified by the `As_RegistersAdditionalServiceContracts` test.
5. **`GetDecorators` is O(N) per resolve** — fine for typical N. Could be pre-indexed in the ctor if a project ships hundreds of decorators.

## Critical count

**0 critical issues.** All five open items are improvements queued for v0.2+, none block release.
