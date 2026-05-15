# Implementation Plan — TechTeaStudio.Injector v0.2.0 + v0.3.0

Two parallel worker invocations:

- **Worker A** owns v0.2.0 (feature additions inside the core library and its test project).
- **Worker B** owns v0.3.0 (a brand-new sidecar package `TechTeaStudio.Injector.Extensions.DependencyInjection` plus its own test project).

The solution file and the v0.3 stub csproj files are pre-created by the orchestrator before workers start, so the two scopes never collide.

## Worker A — v0.2.0 core library

### Feature 1: Open generics

API:

```csharp
builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
builder.Register(typeof(IRepository<User>), typeof(UserRepository)); // closed still works
```

Behaviour:

- Add `IsOpenGeneric` flag (`Type.IsGenericTypeDefinition`) and an `OpenGenericImplementationType` on `Registration` (or reuse `ImplementationType` and the flag).
- New overloads on `IContainerBuilder`:
  - `Register(Type serviceType, Type implementationType)` returning `IRegistrationBuilder`.
  - `Register(Type serviceType)` (self-registration of an open generic).
- `Container.ResolveInternal`: if `serviceType.IsConstructedGenericType` and no closed registration exists, look up the open generic by `serviceType.GetGenericTypeDefinition()`; if found, materialize a closed `Registration` on the fly (cache it).

Tests (new file `OpenGenericsTests.cs`):

- `Repository<User>` resolves from `IRepository<>` → `Repository<>` registration.
- Scoped lifetime preserved across multiple `Resolve<IRepository<User>>()` in the same scope.
- Closed-generic registration overrides open-generic registration when both exist.
- Unregistered open generic throws `ServiceNotRegisteredException`.

### Feature 2: `Lazy<T>` / `Func<T>` / `Func<P, T>` auto-resolution

API:

```csharp
builder.Register<IFoo, Foo>().AsTransient();
container.Resolve<Lazy<IFoo>>();          // wraps the resolve in Lazy
container.Resolve<Func<IFoo>>();          // delegate that resolves on demand
container.Resolve<Func<string, IFoo>>();  // factory with constructor argument
```

Behaviour:

- Add a "synthetic" resolution path in `Container.ResolveInternal` before the "not registered" throw:
  - `Lazy<X>` → `new Lazy<X>(() => Resolve<X>())`.
  - `Func<X>` → `() => Resolve<X>()`.
  - `Func<P1, X>` → `(p1) => CreateInstanceWithArgs(typeof(X), [p1])`. For `Func<P, T>`, the constructor takes one parameter of type `P` plus any number of resolvable parameters. Use the greediest constructor whose remaining params are resolvable.
- Same path applies when `Lazy<X>` / `Func<X>` appears as a constructor parameter.

Tests (new file `LazyFuncTests.cs`):

- `Lazy<IFoo>` defers creation until `.Value`.
- `Func<IFoo>` creates new instance per invocation (transient) / same instance (singleton).
- `Func<string, IFoo>` passes `string` as ctor parameter and resolves the rest.
- Service registered as transient: `Func<IFoo>()` twice yields two instances.

### Feature 3: `IAsyncDisposable` support

API:

```csharp
await using var container = new ContainerBuilder()…Build();
await using var scope = container.CreateScope();
```

Behaviour:

- `IContainer` extends `IAsyncDisposable`.
- `IScope` extends `IAsyncDisposable`.
- `Container.DisposeAsync()` and `Scope.DisposeAsync()` walk owned disposables: if `IAsyncDisposable`, `await DisposeAsync()`; else fallback to `Dispose()`.
- Sync `Dispose()` keeps current behaviour (sync dispose, even of async-disposables — call `.AsTask().GetAwaiter().GetResult()` only if the instance is *only* `IAsyncDisposable` and not `IDisposable`).
- Tracking: when an instance implements `IAsyncDisposable` but **not** `IDisposable`, store it in a separate list so sync `Dispose()` still cleans it up.

Tests (new file `AsyncDisposalTests.cs`):

- `await scope.DisposeAsync()` calls `DisposeAsync` on every async-disposable scoped instance.
- A service that implements both `IDisposable` and `IAsyncDisposable`: `DisposeAsync` is preferred.
- Sync `Dispose()` still works on async-only-disposable instances (calls `DisposeAsync().AsTask().Wait()`).

### Feature 4: Validate-on-build

API:

```csharp
var container = builder.Build(new BuildOptions { ValidateOnBuild = true });
// throws AggregateException with one entry per broken registration.
```

Behaviour:

- New public `BuildOptions` class (sealed; `ValidateOnBuild`, future flags).
- `IContainerBuilder.Build(BuildOptions options)` overload.
- When `ValidateOnBuild = true`: after activator compilation, iterate every registration and attempt resolution into a throwaway scope; aggregate failures into one exception.
- Make sure scoped registrations are validated in a scope (not from root).

Tests (new file `ValidateOnBuildTests.cs`):

- Healthy registrations build cleanly with `ValidateOnBuild = true`.
- A missing dependency surfaces during `Build` (not during the first resolve).
- A circular dependency surfaces during `Build`.

### Feature 5: `Owned<T>`

API:

```csharp
using (var owned = container.Resolve<Owned<IReportBuilder>>())
{
    owned.Value.Build();
} // owned.Dispose() releases the report builder + everything created for it
```

Behaviour:

- New public `Owned<T>` struct/class implementing `IDisposable` + `IAsyncDisposable`.
- When `Owned<X>` is resolved, the container creates a transient sub-scope, resolves `X` from it, and stores both inside `Owned<T>`. Disposing `Owned<T>` disposes the sub-scope.

Tests (new file `OwnedTests.cs`):

- `Owned<IFoo>` exposes a usable `Value` and disposes its inner scope when itself disposed.
- Disposing the owner does not affect the parent scope/container.
- `Owned<IFoo>` works inside `Func<Owned<IFoo>>`.

### Feature 6: `OnActivating` / `OnActivated` lifecycle hooks

API:

```csharp
builder.Register<IFoo, Foo>()
       .OnActivating(f => f.Configure())   // raw instance, before property/method injection
       .OnActivated(f => f.Initialize());  // after property/method injection, before returning
```

Behaviour:

- `IRegistrationBuilder.OnActivating(Action<object>)` / `.OnActivated(Action<object>)` — non-generic `object` form for the interface, plus a generic helper on a concrete `RegistrationBuilder<T>` if convenient.
- Hooks stored on `Registration`; invoked from `Container.CreateInstance` at the right points.

Tests (new file `LifecycleHooksTests.cs`):

- `OnActivating` runs before property/method injection.
- `OnActivated` runs after property/method injection.
- Hooks fire once per activation (transient: every resolve; singleton: once total).

### Feature 7: Child scope with extra registrations

API:

```csharp
using var child = scope.BeginLifetimeScope(b => b.RegisterInstance(request));
var handler = child.Resolve<IRequestHandler>(); // sees `request` plus everything from parent
```

Behaviour:

- `IScope.BeginLifetimeScope(Action<IContainerBuilder>)` — creates a child scope.
- The child scope has its own "extra" registration list; on `Resolve`, child registrations win over parent.
- Disposing the child scope disposes only what the child created. Parent scope remains usable.

Tests (new file `ChildScopeTests.cs`):

- Child scope sees parent registrations.
- Child registration overrides parent for the same service type.
- Disposing the child does not dispose parent.
- Nested children work (grandchild sees parent + grandparent).

### Feature 8: Compiled decorators (from architect's backlog)

Behaviour:

- Replace the `ctor.Invoke(args)` path in `Container.ApplyDecorators` with a compiled `Func<IResolver, object /*inner*/, object /*decorator*/>` lambda built once per decorator registration and cached.

Tests:

- Existing decorator tests should still pass.
- New micro-test in `DecoratorsModulesScanTests.cs`: resolving a decorated service many times reuses the cached decorator activator.

### Feature 9: Build-time decorator validation (from architect's backlog)

Behaviour:

- During `Build()`, every decorator registration must have at least one underlying (non-decorator) registration of the same service type. Otherwise: `RegistrationException`.

Tests:

- New test in `DecoratorsModulesScanTests.cs`: `RegisterDecorator` without a matching base throws on `Build()`.

### Worker A files

Modify:

- `src/TechTeaStudio.Injector/IContainerBuilder.cs`
- `src/TechTeaStudio.Injector/IContainer.cs`
- `src/TechTeaStudio.Injector/IScope.cs`
- `src/TechTeaStudio.Injector/Registration.cs`
- `src/TechTeaStudio.Injector/ContainerBuilder.cs`
- `src/TechTeaStudio.Injector/Container.cs`
- `src/TechTeaStudio.Injector/Scope.cs`
- `src/TechTeaStudio.Injector/Activators.cs`

Create:

- `src/TechTeaStudio.Injector/BuildOptions.cs`
- `src/TechTeaStudio.Injector/Owned.cs`
- `tests/TechTeaStudio.Injector.Tests/OpenGenericsTests.cs`
- `tests/TechTeaStudio.Injector.Tests/LazyFuncTests.cs`
- `tests/TechTeaStudio.Injector.Tests/AsyncDisposalTests.cs`
- `tests/TechTeaStudio.Injector.Tests/ValidateOnBuildTests.cs`
- `tests/TechTeaStudio.Injector.Tests/OwnedTests.cs`
- `tests/TechTeaStudio.Injector.Tests/LifecycleHooksTests.cs`
- `tests/TechTeaStudio.Injector.Tests/ChildScopeTests.cs`

## Worker B — v0.3.0 MEDI sidecar

New project: `TechTeaStudio.Injector.Extensions.DependencyInjection`. The orchestrator pre-creates the empty `.csproj` files and slnx entries; Worker B fills in the code.

### Public surface

```csharp
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

// Pattern 1: ContainerBuilder is the root, import IServiceCollection registrations.
var builder = new ContainerBuilder();
builder.PopulateFromServiceCollection(myServiceCollection);
IContainer container = builder.Build();
IServiceProvider provider = container.AsServiceProvider();

// Pattern 2: Generic Host integration.
var hostBuilder = Host.CreateApplicationBuilder(args);
hostBuilder.ConfigureContainer<IContainerBuilder>(
    new InjectorServiceProviderFactory(),
    cb => cb.RegisterModule(new MyModule()));
```

### Adapters

- `InjectorServiceProvider : IServiceProvider, IDisposable, IAsyncDisposable, IServiceProviderIsService, IKeyedServiceProvider` — wraps `IContainer`. `GetService(Type)` returns `null` on miss (no throw, per MEDI semantics — different from `IResolver.Resolve`).
- `InjectorServiceScope : IServiceScope` — wraps an `IScope`.
- `InjectorServiceScopeFactory : IServiceScopeFactory` — wraps `IContainer.CreateScope`.
- `InjectorServiceProviderFactory : IServiceProviderFactory<IContainerBuilder>` — for `HostBuilder.UseServiceProviderFactory(...)`.

### Helpers

- `IContainerBuilder.PopulateFromServiceCollection(IServiceCollection)` — maps every `ServiceDescriptor` into the right `IContainerBuilder` call:
  - `ServiceLifetime.Singleton` → `AsSingleton`.
  - `ServiceLifetime.Scoped` → `AsScoped`.
  - `ServiceLifetime.Transient` → `AsTransient`.
  - `ImplementationType` → `Register(Type, Type)`.
  - `ImplementationInstance` → `RegisterInstance`.
  - `ImplementationFactory` → factory overload (adapter wraps the `IServiceProvider`-taking factory in an `IResolver`-taking one).
  - `IsKeyedService` (NET 8+): convert key via `WithKey`. Compile only on `net8.0` and `net10.0`; net8.0+ for keyed support; otherwise drop the keyed bits.
- `IContainer.AsServiceProvider()` — convenience extension returning `InjectorServiceProvider`.

### Tests (in `tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests/`)

- `IServiceProvider.GetService<T>()` returns the same instance MEDI users expect.
- `IServiceScope` disposes scoped services.
- `IServiceProviderIsService.IsService(typeof(T))` returns true iff registered.
- Keyed services: `IKeyedServiceProvider.GetKeyedService(typeof(T), "alpha")` works for `.WithKey("alpha")` registrations.
- `PopulateFromServiceCollection` round-trip: register a few `IServiceCollection` entries, resolve them via `InjectorServiceProvider`.
- ASP.NET Core simulation: `HostBuilder.UseServiceProviderFactory(new InjectorServiceProviderFactory())` builds a host. Not actually running ASP.NET — just verifying the factory contract.

### Worker B files (create)

- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/TechTeaStudio.Injector.Extensions.DependencyInjection.csproj` (pre-created by orchestrator).
- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/InjectorServiceProvider.cs`
- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/InjectorServiceScope.cs`
- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/InjectorServiceScopeFactory.cs`
- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/InjectorServiceProviderFactory.cs`
- `src/TechTeaStudio.Injector.Extensions.DependencyInjection/ServiceCollectionExtensions.cs`
- `tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests.csproj` (pre-created by orchestrator).
- `tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests/AdapterTests.cs`
- `tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests/PopulateFromServiceCollectionTests.cs`
- `tests/TechTeaStudio.Injector.Extensions.DependencyInjection.Tests/HostFactoryTests.cs`

## Pipeline stages

1. Orchestrator creates the v0.3 stub csproj files and updates `Injector.slnx`.
2. **Worker A** + **Worker B** run in parallel (no file overlap).
3. **Architect** reviews both worker outputs against the issue body and conventions.
4. **Tester** runs `dotnet build` + `dotnet test` for `Injector.slnx`. Failure means a re-spin.
5. Orchestrator bumps `<Version>` in the core `.csproj` to `0.2.0`, updates `CHANGELOG.md`, `README.md`, and `ARCHITECT_REVIEW.md`.
