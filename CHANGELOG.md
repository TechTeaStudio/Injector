# Changelog

All notable changes to this project are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.1] — 2026-11-11

**Core** (`TechTeaStudio.Injector`).

### Added
- **`net11.0` target** alongside `net8.0`, `net9.0` and `net10.0`. No API or behaviour change.

## [0.3.4] — 2026-11-11

**Sidecar** (`TechTeaStudio.Injector.Extensions.DependencyInjection`). Rebuilt against core 0.5.1.

### Added
- **`net11.0` target** alongside `net8.0`, `net9.0` and `net10.0`. No API or behaviour change.

## [0.5.0] — 2026-10-02

**Core** (`TechTeaStudio.Injector`). Release-blocking engine fixes and keyed-service semantics.

Core versions 0.3.0 and 0.4.0 were never published to nuget.org (the CI publish step failed on a package-validation baseline that does not exist), so 0.5.0 is the first published core version after 0.2.0 and ships everything listed under 0.3.0 and 0.4.0 as well.

### Added
- **`RegisterDecorator(Type serviceType, Type decoratorType)`** on `IContainerBuilder`. Open-generic decorators (`RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>))`) are now reachable from the public API; 0.4.0 advertised the feature but only exposed the generic `RegisterDecorator<TService, TDecorator>()`, which cannot express an open generic. Both arguments must be open generic definitions or neither; the decorator must be a concrete class implementing the service.
- An open-generic decorator now also wraps **closed-only base registrations** (only `IRepository<User>` registered, decorator declared on `IRepository<>`); previously `Build()` rejected that with `RegistrationException`.
- **`ServiceNotRegisteredException.Key`**; the keyed-miss message names the requested key.

### Fixed
- **Failed singleton construction is no longer cached.** 0.4.0's per-registration `Lazy<object>` cached the exception forever; a singleton whose constructor or factory throws is now retried on the next resolve, while concurrent resolvers of the same singleton still share exactly one construction.
- **Singleton cycles throw `CircularDependencyException`.** A same-thread cycle between singletons surfaced as `InvalidOperationException` (re-entrant `Lazy.Value`) in 0.4.0; it now reports the usual chain, for type and factory registrations alike. A cross-thread singleton cycle (thread 1 builds A which needs B while thread 2 builds B which needs A) is detected through a wait-for graph and reported as `CircularDependencyException` instead of deadlocking. Unrelated singletons never serialize on each other's constructor.
- **`ResolveKeyed` / `TryResolveKeyed`** now find keyed open-generic registrations (closed on demand, same as unkeyed resolution) and keyed registrations added to a child scope via `BeginLifetimeScope(b => b.Register<IFoo, Foo>().WithKey("k"))`; a child-scope registration shadows the container, as for unkeyed resolution.
- **`TryResolve` / `ResolveOptional` on synthetic wrappers.** `TryResolve<Owned<T>>` for an unregistered `T` threw `ServiceNotRegisteredException`, and `TryResolve<Lazy<T>>` / `TryResolve<Func<T>>` returned true only to fail on use. All three now answer false / null exactly when `T` itself is unresolvable; a genuine construction failure of a registered `T` still propagates. `Func<P, T>` stays always-resolvable.

### Changed
- **Keyed registrations are invisible to unkeyed resolution.** `Resolve<T>()` no longer returns a `WithKey(...)` registration when it happens to be registered last, `ResolveAll<T>()` / `IEnumerable<T>` no longer include keyed registrations, and `IsRegistered<T>()` (plus `TryResolve` / `ResolveOptional`) answer for unkeyed registrations only. Keyed services stay reachable through `ResolveKeyed`, `TryResolveKeyed`, and `ResolveDictionary`. This matches Microsoft.Extensions.DependencyInjection keyed-service semantics, which the sidecar relies on. An `As(...)` alias of a keyed registration resolves its primary by key.

## [0.3.3] — 2026-10-02

**Sidecar** (`TechTeaStudio.Injector.Extensions.DependencyInjection`). Rebuilt against core 0.5.0.

Sidecar versions 0.3.1 and 0.3.2 were never published to nuget.org (same CI failure as the core), so 0.3.3 is the first published sidecar version after 0.3.0.

### Fixed
- **Package dependency on a non-existent core version.** The published 0.3.0 declares a dependency on `TechTeaStudio.Injector` 0.3.0, which was never published, so it cannot be installed. The CI pack step passes a global `PackageVersion` that leaked into the referenced core project; both src projects now declare `TreatAsLocalProperty="PackageVersion"` and pin it to their own `<Version>`, so 0.3.3 depends on core 0.5.0.

### Changed
- Keyed descriptors imported by `PopulateFromServiceCollection` follow MEDI semantics: `GetService(Type)` / `GetServices<T>()` never return a keyed registration and `IsService(Type)` ignores it; `GetKeyedService` / `GetRequiredKeyedService` / `IsKeyedService` are unchanged. (Behaviour comes from the core change above; the adapter itself is unchanged.)

## [0.4.0] — 2026-05-29

**Core** (`TechTeaStudio.Injector`). Engine hardening, faster resolution, and a richer resolution API.

### Added
- **Public keyed resolution.** `ResolveKeyed<T>(object key)`, `ResolveKeyed(Type, object key)`, and `TryResolveKeyed<T>(object key, out T value)` on `IResolver`, so `WithKey(...)` registrations are reachable from the core API (previously only via the MEDI sidecar or `ResolveDictionary`).
- **Non-throwing resolution.** `TryResolve<T>(out T value)` and `ResolveOptional<T>()` plus `Type` overloads. Only a missing top-level registration yields false/null; a genuine construction failure still propagates.
- **Registration probe.** `IContainer.IsRegistered<T>()` / `IsRegistered(Type)`.
- **Open-generic decorators.** `RegisterDecorator` on an open generic (for example `IPipelineBehavior<,>`) now wraps closed resolutions, closing the decorator implementation over the service's type arguments. Decorators are pre-indexed by service type instead of scanned on every activation.
- **Non-public method injection.** `[Inject]` methods that are non-public are now invoked, matching property-injection behavior.

### Fixed
- **Open-generic synthesis data race.** `_byServiceType` is now a `ConcurrentDictionary`, so lock-free readers are safe while a closed generic is synthesized under concurrent load.
- **Singleton single-construction.** Singletons are constructed exactly once under parallel resolution (per-registration `Lazy<object>`), instead of racing and discarding a duplicate.

### Performance
- Lock-free activator caches (`ConcurrentDictionary`) remove the global lock from the resolve hot path.
- A registration-lookup-before-collection-probe fast path avoids generic-type checks for plain services.
- `Func<T>` / `Lazy<T>` / `Func<P, T>` activators are cached; `Func<P, T>` no longer recompiles its expression on every resolve.

## [0.3.2] — 2026-05-29

**Sidecar** (`TechTeaStudio.Injector.Extensions.DependencyInjection`). Rebuilt against core 0.4.0; the adapter exposes the new keyed/optional resolution and open-generic decorators. Documentation fix.

## [0.3.1] — 2026-05-29

**Sidecar** (`TechTeaStudio.Injector.Extensions.DependencyInjection`).

### Added
- NativeAOT / trimming diagnostics. The adapter enables the trim/AOT analyzers; methods implementing MEDI interfaces (not `Requires*`-annotated upstream) carry targeted `[UnconditionalSuppressMessage]` with justifications. The adapter drives the expression-tree-compiled core and is not trim/AOT-safe at runtime.

## [0.3.0] — 2026-05-29

**Core** (`TechTeaStudio.Injector`).

### Added
- **Captive-dependency detection.** Opt in via `builder.Build(new BuildOptions { ValidateScopes = true })` to flag a singleton that captures a scoped service, directly or transitively through its constructor parameters, at build time. Each offending singleton raises a `CaptiveDependencyException`; all are aggregated into one `AggregateException`. Mirrors MEDI's `ValidateScopes`; a transient captured by a singleton is tolerated. Open generics are skipped.
- **NativeAOT / trimming diagnostics.** The public API is annotated with `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` and the project enables the trim/AOT analyzers, so compiling under `PublishAot` / `PublishTrimmed` surfaces accurate warnings instead of silent runtime failures. The container remains expression-tree based and is not AOT/trim-safe at runtime.
- **SourceLink and symbol packages.** Packages now ship deterministic builds, `Microsoft.SourceLink.GitHub`, embedded untracked sources, and `.snupkg` symbol packages for source-stepping. CI builds set `ContinuousIntegrationBuild` and run NuGet package validation against the prior published baseline.

### Internal
- BenchmarkDotNet suite under `benchmarks/` comparing resolve and build throughput against MEDI and Autofac; results are published in the README.

## [0.2.0] — 2026-05-15

Major feature wave aiming at Autofac feature parity plus closing the most-requested MEDI gaps.

### Added

- **Open generics.** Register an open generic implementation against an open generic service:
  ```csharp
  builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
  ```
  Closed generics are synthesized on demand and cached. Closed-generic registrations still take precedence when both are present.
- **`Lazy<T>` / `Func<T>` / `Func<P, T>` auto-resolution.** No registration needed — request any of these for a registered service and the container hands back the right wrapper. `Func<P, T>` selects the greediest constructor whose remaining parameters are resolvable.
- **`IAsyncDisposable` support.** `IContainer` and `IScope` now extend `IAsyncDisposable`. Use `await using var container = builder.Build();` to deterministically dispose async resources. Sync `Dispose()` still works and falls back to `DisposeAsync().AsTask().GetAwaiter().GetResult()` for instances that implement only `IAsyncDisposable`.
- **Validate-on-build.** Opt in via `builder.Build(new BuildOptions { ValidateOnBuild = true })` to walk every registration's dependency tree at `Build` time and surface missing-deps / cycles as a single `AggregateException`. Open-generic registrations and `Func<P, T>` factories are skipped (deferred to v0.3.x).
- **`Owned<T>`.** Autofac-style ownership transfer: `var owned = container.Resolve<Owned<IReportBuilder>>(); owned.Value.Build(); owned.Dispose();` releases the inner instance and every disposable it pulled in, independent of any caller-side scope.
- **`OnActivating` / `OnActivated` lifecycle hooks.** Per-registration callbacks: `OnActivating` runs before property/method injection, `OnActivated` after. Hooks fire once per activation (once total for singletons, per resolve for transients).
- **Child scopes with extra registrations.** `scope.BeginLifetimeScope(b => b.RegisterInstance(request))` creates a child scope that shadows the parent for the supplied registrations and disposes only what it created.
- **Compiled decorator activators.** Decorators are no longer instantiated via `ConstructorInfo.Invoke`; they go through `Expression.Lambda`-compiled `Func<IResolver, object, object>` cached per decorator registration. Materially faster for hot-path decorated services.
- **Build-time decorator validation.** `ContainerBuilder.Build()` now throws `RegistrationException` if any `RegisterDecorator<TService, TDecorator>()` has no underlying registration of `TService`.

### Internal

- New `internal` surface on `Container` (`IsRegisteredInternal`, `GetAllRegistrations`, `FindKeyedRegistration`, `ResolveKeyed`) reachable from the v0.3 sidecar via `[assembly: InternalsVisibleTo("TechTeaStudio.Injector.Extensions.DependencyInjection")]`. The sidecar uses these typed hooks instead of reflection.

### Backward compatibility

- Every v0.1 test still passes (43 tests baseline) and every existing public signature is preserved. The change to `IContainer` / `IScope` to extend `IAsyncDisposable` is a binary-compatible interface broadening for consumers; the library only ships sealed concrete implementations, so the practical impact is limited to mock-implementers of those interfaces.

### Tests

- Core test suite grows from 43 to **82** tests (+39 across `OpenGenericsTests`, `LazyFuncTests`, `AsyncDisposalTests`, `ValidateOnBuildTests`, `OwnedTests`, `LifecycleHooksTests`, `ChildScopeTests`, plus two new tests in `DecoratorsModulesScanTests` for compiled decorator reuse and build-time decorator validation).

## [0.3.0] — 2026-05-15

New sidecar package: **`TechTeaStudio.Injector.Extensions.DependencyInjection`** — a Microsoft.Extensions.DependencyInjection adapter that plugs the Injector container into ASP.NET Core / Generic Host / EF Core.

### Added

- **`InjectorServiceProvider`** — implements `IServiceProvider`, `IServiceProviderIsService`, `IServiceProviderIsKeyedService`, `IKeyedServiceProvider`, `IDisposable`, `IAsyncDisposable`. `GetService(Type)` returns `null` on miss (MEDI semantics). Recognizes `IEnumerable<T>` / `IReadOnlyCollection<T>` / `IReadOnlyList<T>` / `ICollection<T>` / `IList<T>` / `Lazy<T>` / `Func<T>` / `Owned<T>` in `IsService` when the inner type is registered.
- **`InjectorServiceScope`** — wraps an `IScope`.
- **`InjectorServiceScopeFactory`** — wraps `IContainer.CreateScope`.
- **`InjectorServiceProviderFactory`** — implements `IServiceProviderFactory<IContainerBuilder>` for `hostBuilder.UseServiceProviderFactory(...)`. Optional `(BuildOptions?)` constructor overload forwards validation knobs to `Build`.
- **`ServiceCollectionExtensions.PopulateFromServiceCollection(this IContainerBuilder, IServiceCollection)`** — round-trips every `ServiceDescriptor` shape, including all three keyed variants (NET 8+): non-keyed × `{Singleton, Scoped, Transient}` × `{ImplementationType, ImplementationInstance, ImplementationFactory}` and the keyed mirrors.
- **`ServiceCollectionExtensions.AsServiceProvider(this IContainer)`** — convenience adapter. Non-owning: disposing the wrapper does not dispose the container.

### Disposal ownership semantics

- `container.AsServiceProvider()` — non-owning. Dispose the container yourself.
- `new InjectorServiceProviderFactory().CreateServiceProvider(builder)` — **owning**. Host disposal propagates to the container, matching MEDI's lifetime contract.

### Tests

- 45 sidecar tests across `AdapterTests` (intrinsic resolution, `IsService` parity with MEDI for collections / `Lazy` / `Func` / `Owned`, keyed services, `IServiceProviderIsKeyedService`), `PopulateFromServiceCollectionTests` (all 12 descriptor variants), and `HostFactoryTests` (Generic Host integration, ownership semantics, `BuildOptions` plumbing).
- Solution-wide total: **127** passing tests.

### Known limitations (scheduled for v0.3.1)

- `Func<P, T>` activators are not cached; the compiled lambda is rebuilt on every resolve.
- `Func<P, T>` ambiguous constructor selection silently picks the first match.
- Open-generic registrations are skipped by `ValidateOnBuild`.
- Child scopes silently ignore decorators registered on the child builder.
- `Owned<T>` resolved from the root container creates a parentless scope; documented behaviour but worth a doc pass.

## [0.1.0] — 2026-05-15

### Added
- Initial release. Lightweight DI container with:
  - Fluent registration via `IContainerBuilder` (`Register<TService, TImpl>`, `Register<T>(factory)`, `RegisterInstance`, `RegisterDecorator`, `RegisterModule`, `RegisterAssembly`).
  - Three lifetimes: `AsTransient()`, `AsScoped()`, `AsSingleton()`.
  - Constructor injection, property injection, and method injection via `[Inject]`.
  - Decorator chaining (innermost = first registered).
  - Circular dependency detection with full chain in `CircularDependencyException`.
  - `IEnumerable<T>` resolution (`ResolveAll`) and keyed `IDictionary<TKey, T>` resolution (`ResolveDictionary`).
  - Expression-tree compiled activators cached per `(serviceType, lifetime)` for fast resolution.
  - `IScope` with deterministic disposal of every scoped `IDisposable` it owns.
  - `IModule` abstraction and assembly scanning with a predicate filter.
- `TechTeaStudio.Injector.Sample` console demo (logger + service duo) and a 43-test xUnit suite.
- `Directory.Build.props`, `dotnet.yml` CI on `product`, README, LICENSE, `.gitignore`, project CLAUDE.md, and PLAN.md.
