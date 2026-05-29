using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Singleton resolution from the root container/provider. Measures the cached
/// lookup path: the instance is created once, every resolve returns it.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("SingletonResolve")]
public class SingletonResolveBenchmarks
{
    private IContainer _injector = null!;
    private IServiceProvider _medi = null!;
    private Autofac.IContainer _autofac = null!;

    [GlobalSetup]
    public void Setup()
    {
        var ib = new ContainerBuilder();
        ib.Register<ISingletonService, SingletonService>().AsSingleton();
        _injector = ib.Build();
        _ = _injector.Resolve<ISingletonService>(); // warm the singleton cache

        var sc = new ServiceCollection();
        sc.AddSingleton<ISingletonService, SingletonService>();
        _medi = sc.BuildServiceProvider();
        _ = _medi.GetRequiredService<ISingletonService>();

        var ab = new Autofac.ContainerBuilder();
        ab.RegisterType<SingletonService>().As<ISingletonService>().SingleInstance();
        _autofac = ab.Build();
        _ = _autofac.Resolve<ISingletonService>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _injector.Dispose();
        (_medi as IDisposable)?.Dispose();
        _autofac.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Injector")]
    public ISingletonService Injector() => _injector.Resolve<ISingletonService>();

    [Benchmark(Description = "MEDI")]
    public ISingletonService Medi() => _medi.GetRequiredService<ISingletonService>();

    [Benchmark(Description = "Autofac")]
    public ISingletonService Autofac() => _autofac.Resolve<ISingletonService>();
}
