# Changelog

All notable changes to this project are documented here.
Format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
- `TechTeaStudio.Injector.Sample` console demo (logger + service duo) and a 70+-test xUnit suite.
- `Directory.Build.props`, `dotnet.yml` CI on `product`, README, LICENSE, `.gitignore`, project CLAUDE.md, and PLAN.md.
