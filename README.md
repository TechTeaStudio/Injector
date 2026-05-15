# TechTeaStudio.Injector

A lightweight, expression-tree-compiled dependency injection container for .NET 8 / 9 / 10. Built from scratch with no dependency on `Microsoft.Extensions.DependencyInjection` in the core library. A dedicated sidecar package (`TechTeaStudio.Injector.Extensions.DependencyInjection`) plugs it into ASP.NET Core / Generic Host / EF Core when you need them.

## What's in v0.2.0 + v0.3.0

### Core library (`TechTeaStudio.Injector`)

- **Fluent registration:** `builder.Register<IFoo, Foo>().AsSingleton();`
- **Three lifetimes:** transient, scoped, singleton.
- **Constructor / property / method injection** (the latter two via `[Inject]`).
- **Open generics:** `builder.Register(typeof(IRepo<>), typeof(Repo<>)).AsScoped()`. Closed generics synthesized on demand.
- **`Lazy<T>` / `Func<T>` / `Func<P, T>` auto-resolution.** No registration needed.
- **`Owned<T>`** ownership transfer (Autofac-style).
- **Decorators** with compiled activators, composed in registration order.
- **Modules:** bundle related registrations behind an `IModule`.
- **Assembly scanning:** `RegisterAssembly(asm, filter)` for convention-based bulk wiring.
- **Collection resolution:** `ResolveAll<T>()` returns `IEnumerable<T>`; `ResolveDictionary<TKey, T>()` returns keyed services.
- **`OnActivating` / `OnActivated` lifecycle hooks.**
- **Child scopes with extra registrations:** `scope.BeginLifetimeScope(b => …)`.
- **Validate-on-build:** `builder.Build(new BuildOptions { ValidateOnBuild = true })`.
- **`IAsyncDisposable`** on `IContainer` and `IScope`.
- **Circular dependency detection** with the full chain in `CircularDependencyException`.
- **Expression-tree compiled activators** cached per `(serviceType, lifetime)`.

### Sidecar package (`TechTeaStudio.Injector.Extensions.DependencyInjection`)

- Implements `IServiceProvider`, `IServiceScope`, `IServiceScopeFactory`, `IServiceProviderIsService`, `IServiceProviderIsKeyedService`, `IKeyedServiceProvider`.
- `IServiceProviderFactory<IContainerBuilder>` for `hostBuilder.UseServiceProviderFactory(new InjectorServiceProviderFactory())`.
- `PopulateFromServiceCollection` round-trips every `ServiceDescriptor` shape (all 12 variants including the keyed mirrors).

## Build & test

```bash
dotnet build Injector.slnx
dotnet test  Injector.slnx
```

Requires .NET SDK 10. The core library multi-targets `net8.0;net9.0;net10.0`; the sidecar does the same. The sample and the test projects are `net10.0` only.

127 tests pass across the two test projects (82 core + 45 sidecar).

## Quick start (core)

```csharp
using TechTeaStudio.Injector;

var builder = new ContainerBuilder();

builder.Register<ILogger, ConsoleLogger>().AsSingleton();
builder.Register<IGreeter, Greeter>().AsTransient();
builder.RegisterDecorator<IGreeter, TimingGreeterDecorator>();

// Open generics:
builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();

// Lifecycle hooks:
builder.Register<IDbInit, DbInit>()
       .AsSingleton()
       .OnActivated(_ => Console.WriteLine("DbInit constructed"));

await using IContainer container = builder.Build(new BuildOptions { ValidateOnBuild = true });

var greeter = container.Resolve<IGreeter>();
greeter.Greet("World");

await using (IScope scope = container.CreateScope())
{
    var repo = scope.Resolve<IRepository<User>>(); // closed-on-demand
    var lazy = scope.Resolve<Lazy<IGreeter>>();    // auto-wrapped

    using (Owned<IReportBuilder> owned = scope.Resolve<Owned<IReportBuilder>>())
    {
        owned.Value.Build();
    } // releases the owned graph
}
```

## Quick start (ASP.NET Core / Generic Host)

```csharp
using Microsoft.Extensions.Hosting;
using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

var host = Host.CreateApplicationBuilder(args);

host.Services.AddSingleton<ILogger, ConsoleLogger>(); // standard IServiceCollection still works

host.ConfigureContainer(
    new InjectorServiceProviderFactory(),
    builder =>
    {
        builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
        builder.RegisterDecorator<IGreeter, TimingGreeterDecorator>();
    });

using var app = host.Build();
// All ASP.NET Core / EF Core / Hosting now resolves through TechTeaStudio.Injector under the hood.
```

## Lifetimes at a glance

| Lifetime | Per-resolution behaviour | When to use |
| --- | --- | --- |
| `AsTransient()` | New instance every call. | Default for cheap, stateless services. |
| `AsScoped()` | Same instance within a single `IScope`. | Per-request / per-operation state. |
| `AsSingleton()` | Single instance for the whole container's lifetime. | Caches, configuration, expensive shared deps. |

## Release flow

CI builds and tests on every push / PR to the `product` branch. The two NuGet packages are published from `product`:

- `TechTeaStudio.Injector` (core)
- `TechTeaStudio.Injector.Extensions.DependencyInjection` (sidecar)

## Comparison with Autofac and Microsoft.Extensions.DependencyInjection

| Capability | Injector | Autofac | MEDI |
| --- | --- | --- | --- |
| Constructor injection | yes | yes | yes |
| Property / method injection | yes (`[Inject]`) | yes (`PropertiesAutowired`) | no |
| Decorators (built-in) | yes (compiled) | yes | no (Scrutor needed) |
| Modules / assembly scan | yes | yes | no (Scrutor needed) |
| Open generics | yes | yes | yes |
| `Lazy<T>` / `Func<T>` / `Func<P, T>` | yes (auto) | yes | no |
| `Owned<T>` | yes | yes | no |
| Lifecycle hooks (`OnActivating` / `OnActivated`) | yes | yes | no |
| Child scopes with extra registrations | yes | yes | no |
| `IEnumerable<T>` / `IReadOnlyDictionary<TKey,T>` | yes | yes | partial (`IEnumerable<T>` only) |
| Validate-on-build | yes (opt-in) | partial | yes |
| `IAsyncDisposable` | yes | yes | yes |
| Circular detection with full chain | yes | partial | partial |
| ASP.NET Core / Hosting integration | via sidecar | via `Autofac.Extensions.DependencyInjection` | native |
| Keyed services (.NET 8 `[FromKeyedServices]`) | yes (via sidecar) | yes | yes |
| NativeAOT | not yet (v1.0 roadmap) | partial | yes |

Picking Injector makes sense when you want Autofac's feature set without Autofac's surface area or 15 years of historical baggage, or when you want a from-scratch container with no transitive `Microsoft.Extensions.*` dependency in the core.

## License

MIT. See `LICENSE.txt`.
