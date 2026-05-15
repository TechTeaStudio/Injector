# Implementation Plan — TechTeaStudio.Injector

This file partitions the MVP implementation into four mostly-independent scopes so four worker invocations can run in parallel. The skeleton (this commit) compiles end-to-end with `NotImplementedException` stubs, so workers can drop their implementations in without breaking the rest.

## Scope A — Registration API (`ContainerBuilder` + lifetime metadata)

**Owner files**

- `src/TechTeaStudio.Injector/ContainerBuilder.cs` — replace stub bodies with real ones.
- `src/TechTeaStudio.Injector/Registration.cs` — **new** file. Internal record describing one registration.

**Surface**

- All `IContainerBuilder` methods except `Build()` itself fully wired:
  - `Register<TService, TImpl>()`
  - `Register<TService>()`
  - `Register<TService>(Func<IResolver, TService>)`
  - `RegisterInstance<TService>(TService)`
- `IRegistrationBuilder` returned by every method, with `AsTransient` / `AsScoped` / `AsSingleton` / `WithKey(object)` / `As(params Type[])`.
- `RegisterInstance` defaults to `Singleton` and pre-fills the cached instance.

**Tests** — `tests/TechTeaStudio.Injector.Tests/RegistrationApiTests.cs`

- Each `Register*` overload produces a registration matching the input.
- `As(Type...)` adds extra service-type aliases.
- Switching lifetime via `AsTransient` / `AsScoped` / `AsSingleton` is observable.
- `WithKey` attaches a key. Duplicate keys for the same service type throw `RegistrationException` on `Build`.

## Scope B — Resolution engine (`Container`, expression-tree activators)

**Owner files**

- `src/TechTeaStudio.Injector/Container.cs` — **new** file. Implements `IContainer` + `IResolver`.
- `src/TechTeaStudio.Injector/ContainerBuilder.cs` — implement `Build()` to construct and return a `Container`. (Coordinate with Worker A — Worker B only fills `Build()`, never touches the `Register*` methods.)

**Behaviour**

- For each registered service type, on first resolve build an `Expression<Func<IResolver, object>>`:
  1. Walk the chosen constructor's parameters.
  2. Recursively compile each parameter's activator (cached).
  3. Add the property / method injection passes (`[Inject]`).
  4. Wrap with decorator chain (if any).
  5. Compile to `Func<IResolver, object>`. Cache by `(serviceType, lifetime)`.
- Use a *visit set* (`HashSet<Type>` + ordered stack) during compilation to detect cycles; throw `CircularDependencyException` with the full chain.
- Constructor selection: greediest constructor whose parameters can all be resolved.
- Thread-safety: cache writes guarded by a single `lock`. Compiled delegates themselves are immutable.

**Tests** — `tests/TechTeaStudio.Injector.Tests/ResolutionEngineTests.cs`

- Transient yields different instances; singleton yields the same instance.
- Constructor injection picks the greediest resolvable ctor.
- Direct A→B→A and indirect A→B→C→A cycles both throw `CircularDependencyException` with the chain.
- `ServiceNotRegisteredException` is thrown for an unregistered type.
- Compiled activators are reused (second resolve does NOT recompile — assert via a Stopwatch sanity check or hook into a public `CompilationCount` test seam).

## Scope C — Scope / disposal / collection resolution

**Owner files**

- `src/TechTeaStudio.Injector/Scope.cs` — **new** file. Implements `IScope`.
- `src/TechTeaStudio.Injector/Container.cs` — small additions: `CreateScope()`, `ResolveAll`, `ResolveDictionary`. (Coordinate with Worker B.)

**Behaviour**

- `IScope` owns: parent container reference, dictionary of scoped instances, list of `IDisposable`s.
- Scoped resolution: if cached → return; else compile + activate + cache + (if `IDisposable`) push onto the disposal list.
- `Dispose()` releases every `IDisposable` it owns, in reverse-creation order, then marks itself disposed (further resolves throw `ObjectDisposedException`).
- Disposal is idempotent.
- `ResolveAll<T>()` and `ResolveAll(Type)` return registrations in *registration order*. Decorators apply per-registration, not across the collection.
- `ResolveDictionary<TKey, T>()` returns only registrations that were tagged with `WithKey`. Empty when none.

**Tests** — `tests/TechTeaStudio.Injector.Tests/ScopeAndCollectionsTests.cs`

- Scoped instance is the same within a scope, different across scopes.
- Disposing a scope disposes every scoped `IDisposable` it created. Idempotent.
- Resolving from a disposed scope throws.
- `ResolveAll<IFoo>()` returns all registrations in order; mixing transient + singleton works.
- `ResolveDictionary<string, IHandler>()` returns only the keyed registrations.

## Scope D — Decorators / `[Inject]` / modules / assembly scanning + the Sample project

**Owner files**

- `src/TechTeaStudio.Injector/Activators.cs` — **new** file. Helpers used by Worker B for decorator composition and `[Inject]` discovery / application.
- `src/TechTeaStudio.Injector/ContainerBuilder.cs` — fill in `RegisterDecorator`, `RegisterModule`, `RegisterAssembly`.
- `samples/TechTeaStudio.Injector.Sample/Program.cs` — replace the placeholder with a real "logger + service" demo.

**Behaviour**

- `RegisterDecorator<TService, TDecorator>()` records a decorator type bound to `TService`. During `Build`, every registration of `TService` gets its activator wrapped (innermost = first registered).
- `RegisterModule(IModule)` invokes the module's `Register` against the same builder.
- `RegisterAssembly(asm, filter, lifetime)` enumerates `asm.GetTypes()` where:
  - Type is `class`, not abstract, not generic-open, public.
  - `filter(type)` returns true (default: anything).
  - Registers against the type itself **and** every interface it implements (other than `IDisposable`, `IAsyncDisposable`).
- `[Inject]` on a settable property: after construction, the container resolves the property's type and assigns it.
- `[Inject]` on a public method: after construction (and after property injection), the container resolves each parameter and invokes the method.

**Tests** — `tests/TechTeaStudio.Injector.Tests/DecoratorsModulesScanTests.cs`

- Two decorators applied in order produce `new D2(new D1(impl))`.
- Module's `Register` is called exactly once when registered.
- `RegisterAssembly` picks up implementations matching a predicate and registers them against the right interfaces.
- `[Inject]` property is populated.
- `[Inject]` method is invoked with resolved parameters.
- Sample project's `Program.cs` builds and runs (compile-only test is enough).

## Coordination notes

- Workers A and B both touch `ContainerBuilder.cs`. To minimize merge conflict risk, **Worker B should rebase its changes onto Worker A's output**. In practice, the orchestrator runs them sequentially in this skeleton's case.
- Workers C and B both touch `Container.cs`. Same rebase note.
- Worker D's edits to `ContainerBuilder.cs` are additive — its three methods (`RegisterDecorator`, `RegisterModule`, `RegisterAssembly`) don't overlap with Worker A's signatures.

To keep things simple, the orchestrator may run the workers strictly sequentially: A → B → C → D. The parallelism is a future optimisation.
