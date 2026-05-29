using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Scoped resolution: create a scope, resolve a scoped service (which itself
/// pulls a transient dependency), then dispose the scope. Mirrors a per-request
/// resolution pattern across the three containers.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("ScopedResolve")]
public class ScopedResolveBenchmarks
{
    private IContainer _injector = null!;
    private ServiceProvider _medi = null!;
    private Autofac.IContainer _autofac = null!;

    [GlobalSetup]
    public void Setup()
    {
        var ib = new ContainerBuilder();
        ib.Register<ILevel0, Level0>().AsTransient();
        ib.Register<IScopedService, ScopedService>().AsScoped();
        _injector = ib.Build();

        var sc = new ServiceCollection();
        sc.AddTransient<ILevel0, Level0>();
        sc.AddScoped<IScopedService, ScopedService>();
        _medi = sc.BuildServiceProvider();

        var ab = new Autofac.ContainerBuilder();
        ab.RegisterType<Level0>().As<ILevel0>().InstancePerDependency();
        ab.RegisterType<ScopedService>().As<IScopedService>().InstancePerLifetimeScope();
        _autofac = ab.Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _injector.Dispose();
        _medi.Dispose();
        _autofac.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Injector")]
    public IScopedService Injector()
    {
        using IScope scope = _injector.CreateScope();
        return scope.Resolve<IScopedService>();
    }

    [Benchmark(Description = "MEDI")]
    public IScopedService Medi()
    {
        using IServiceScope scope = _medi.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IScopedService>();
    }

    [Benchmark(Description = "Autofac")]
    public IScopedService Autofac()
    {
        using ILifetimeScope scope = _autofac.BeginLifetimeScope();
        return scope.Resolve<IScopedService>();
    }
}
