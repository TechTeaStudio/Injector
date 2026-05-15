# Quickstart — TechTeaStudio.Injector

Lightweight, expression-tree-compiled DI container. Constructor / property / method injection, transient / scoped / singleton lifetimes, decorators, modules, assembly scanning, circular-dependency detection, keyed registrations, `IEnumerable<T>` / `IDictionary<TKey, T>` resolution.

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
Expected: 43 passed, 0 failed.

## Run the sample
```powershell
dotnet run --project samples/TechTeaStudio.Injector.Sample
```
Demonstrates registration of a logger + a service that consumes it.

## Use it in your own app
```csharp
var builder = new ContainerBuilder();

builder.Register<ILogger, ConsoleLogger>().AsSingleton();
builder.Register<IService, MyService>().AsScoped();
builder.RegisterDecorator<IService, LoggingDecorator>();
builder.RegisterAssembly(typeof(Program).Assembly, t => t.Name.EndsWith("Handler"));

await using var container = builder.Build();
await using var scope     = container.CreateScope();

var svc = scope.Resolve<IService>();          // wrapped in LoggingDecorator
var all = scope.ResolveAll<IService>();       // every registration
var by  = scope.ResolveDictionary<string, IHandler>();  // keyed registrations
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

## Exceptions
- `CircularDependencyException` — graph cycle; the exception message lists the full dependency chain.
- `RegistrationException`, `ResolutionException` — fluent API misuse or unresolvable service.
