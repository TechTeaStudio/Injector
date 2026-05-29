using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Transient resolution of a 3-level constructor dependency chain
/// (<see cref="ILevel2"/> -> <see cref="ILevel1"/> -> <see cref="ILevel0"/>).
/// Each resolve builds a fresh graph.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("TransientResolve")]
public class TransientResolveBenchmarks
{
    private IContainer _injector = null!;
    private IServiceProvider _medi = null!;
    private Autofac.IContainer _autofac = null!;

    [GlobalSetup]
    public void Setup()
    {
        var ib = new ContainerBuilder();
        ib.Register<ILevel0, Level0>().AsTransient();
        ib.Register<ILevel1, Level1>().AsTransient();
        ib.Register<ILevel2, Level2>().AsTransient();
        _injector = ib.Build();

        var sc = new ServiceCollection();
        sc.AddTransient<ILevel0, Level0>();
        sc.AddTransient<ILevel1, Level1>();
        sc.AddTransient<ILevel2, Level2>();
        _medi = sc.BuildServiceProvider();

        var ab = new Autofac.ContainerBuilder();
        ab.RegisterType<Level0>().As<ILevel0>().InstancePerDependency();
        ab.RegisterType<Level1>().As<ILevel1>().InstancePerDependency();
        ab.RegisterType<Level2>().As<ILevel2>().InstancePerDependency();
        _autofac = ab.Build();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _injector.Dispose();
        (_medi as IDisposable)?.Dispose();
        _autofac.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Injector")]
    public ILevel2 Injector() => _injector.Resolve<ILevel2>();

    [Benchmark(Description = "MEDI")]
    public ILevel2 Medi() => _medi.GetRequiredService<ILevel2>();

    [Benchmark(Description = "Autofac")]
    public ILevel2 Autofac() => _autofac.Resolve<ILevel2>();
}
