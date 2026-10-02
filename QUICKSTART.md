# Quickstart — TechTeaStudio.Injector

Lightweight, expression-tree-compiled DI container. Constructor / property / method injection, transient / scoped / singleton lifetimes, decorators, modules, assembly scanning, circular-dependency detection, keyed registrations, `IEnumerable<T>` / `IDictionary<TKey, T>` resolution. Plus open generics, `Lazy<T>` / `Func<T>` / `Func<P, T>` / `Owned<T>`, lifecycle hooks, child scopes, `IAsyncDisposable`, and validate-on-build (v0.2). A sidecar package (`TechTeaStudio.Injector.Extensions.DependencyInjection`) provides `IServiceProvider` adapters for ASP.NET Core / Generic Host integration (v0.3).

## Prerequisites
- .NET SDK 10.0+

## Build
```powershell
dotnet build Injector.slnx
```

## Test
```powershell
dotnet test Injector.slnx
```
Expected: 127 passed, 0 failed (82 core + 45 sidecar).

## Run the sample
```powershell
dotnet run --project samples/TechTeaStudio.Injector.Sample
```
Demonstrates registration of a logger + a service that consumes it.

## Use it in your own app (core)
```csharp
var builder = new ContainerBuilder();

builder.Register<ILogger, ConsoleLogger>().AsSingleton();
builder.Register<IService, MyService>().AsScoped();
builder.RegisterDecorator<IService, LoggingDecorator>();
builder.RegisterAssembly(typeof(Program).Assembly, t => t.Name.EndsWith("Handler"));

// Open generics (and an open-generic decorator over them):
builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
builder.RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>));

// Lifecycle hook:
builder.Register<IDbInit, DbInit>()
       .AsSingleton()
       .OnActivated(_ => Console.WriteLine("DbInit constructed"));

await using var container = builder.Build(new BuildOptions { ValidateOnBuild = true });
await using var scope     = container.CreateScope();

var svc   = scope.Resolve<IService>();                // wrapped in LoggingDecorator
var all   = scope.ResolveAll<IService>();             // every unkeyed registration
var byKey = scope.ResolveDictionary<string, IHandler>(); // keyed registrations (WithKey) only
var one   = scope.ResolveKeyed<IHandler>("alpha");    // a single keyed registration
var repo  = scope.Resolve<IRepository<User>>();       // closed-on-demand from open generic
var lazy  = scope.Resolve<Lazy<IService>>();          // auto-wrapped

using (var owned = scope.Resolve<Owned<IReportBuilder>>())
{
    owned.Value.Build();
} // releases the owned graph
```

## ASP.NET Core / Generic Host (sidecar)
```csharp
using Microsoft.Extensions.Hosting;
using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

var host = Host.CreateApplicationBuilder(args);

host.Services.AddSingleton<ILogger, ConsoleLogger>(); // standard MEDI registrations still work

host.ConfigureContainer(
    new InjectorServiceProviderFactory(new BuildOptions { ValidateOnBuild = true }),
    builder =>
    {
        builder.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
        builder.RegisterDecorator<IService, LoggingDecorator>();
    });

using var app = host.Build();
```

## Module pattern
```csharp
public sealed class LoggingModule : IModule
{
    public void Register(IContainerBuilder b)
    {
        b.Register<ILogger, ConsoleLogger>().AsSingleton();
    }
}

builder.RegisterModule(new LoggingModule());
```

## Attribute-driven property / method injection
```csharp
public class Worker
{
    [Inject] public ILogger? Logger { get; set; }

    [Inject]
    public void Initialise(IService service) { /* ... */ }
}
```

## Child scopes with extra registrations
```csharp
await using var scope = container.CreateScope();

using (var child = scope.BeginLifetimeScope(b => b.RegisterInstance(currentRequest)))
{
    var handler = child.Resolve<IRequestHandler>(); // sees `currentRequest` + parent registrations
}
```

## Exceptions
- `CircularDependencyException` — graph cycle; the exception message lists the full dependency chain.
- `ServiceNotRegisteredException` — requested type has no matching registration (for `ResolveKeyed`, no registration with that key; the message and `Key` name it).
- `RegistrationException` — fluent API misuse (e.g., decorator without a base registration, duplicate keys).
- `InjectorException` — base type for everything above.
