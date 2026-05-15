# TechTeaStudio.Injector

A lightweight, expression-tree-compiled dependency injection container for .NET 8 / 9 / 10. Built from scratch — no dependency on `Microsoft.Extensions.DependencyInjection`.

## Highlights

- **Fluent registration:** `builder.Register<IFoo, Foo>().AsSingleton();`
- **Three lifetimes:** transient, scoped, singleton.
- **Multiple injection styles:** constructor, property (via `[Inject]`), and method (via `[Inject]`).
- **Decorators:** `RegisterDecorator<IFoo, LoggingFooDecorator>()` — composed in registration order.
- **Modules:** bundle related registrations behind an `IModule`.
- **Assembly scanning:** `RegisterAssembly(asm, filter)` for convention-based bulk wiring.
- **Collection resolution:** `ResolveAll<T>()` returns `IEnumerable<T>`; `ResolveDictionary<TKey, T>()` returns keyed services.
- **Circular dependency detection:** throws `CircularDependencyException` with the full chain.
- **Fast:** activators are built as expression trees and compiled to delegates, then cached.

## Build & test

```bash
dotnet build Injector.slnx
dotnet test  Injector.slnx
```

Requires .NET SDK 10. The library multi-targets `net8.0;net9.0;net10.0`; the sample and tests are `net10.0` only.

## Quick start

```csharp
using TechTeaStudio.Injector;

var builder = new ContainerBuilder();

builder.Register<ILogger, ConsoleLogger>().AsSingleton();
builder.Register<IGreeter, Greeter>().AsTransient();
builder.RegisterDecorator<IGreeter, TimingGreeterDecorator>();

using IContainer container = builder.Build();

var greeter = container.Resolve<IGreeter>();
greeter.Greet("World"); // Timed by the decorator, logs via the singleton logger.

using (IScope scope = container.CreateScope())
{
    var scoped = scope.Resolve<IUnitOfWork>(); // disposed when the scope ends
}
```

## Lifetimes at a glance

| Lifetime | Per-resolution behaviour | When to use |
| --- | --- | --- |
| `AsTransient()` | New instance every call. | Default for cheap, stateless services. |
| `AsScoped()` | Same instance within a single `IScope`. | Per-request / per-operation state. |
| `AsSingleton()` | Single instance for the whole container's lifetime. | Caches, configuration, expensive shared deps. |

## Release flow

CI builds and tests on every push / PR to the `product` branch. This is published as a NuGet package in the `0.x` line.

## License

MIT. See `LICENSE.txt`.
