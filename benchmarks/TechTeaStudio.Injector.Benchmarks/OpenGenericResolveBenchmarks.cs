using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Open-generic resolution: <c>IRepository&lt;Customer&gt;</c> closed on demand from
/// the open-generic registration <c>IRepository&lt;&gt; -&gt; Repository&lt;&gt;</c>. The closed
/// registration is warmed once in setup so this measures the cached fast path.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("OpenGenericResolve")]
public class OpenGenericResolveBenchmarks
{
    private IContainer _injector = null!;
    private IServiceProvider _medi = null!;
    private Autofac.IContainer _autofac = null!;

    [GlobalSetup]
    public void Setup()
    {
        var ib = new ContainerBuilder();
        ib.Register(typeof(IRepository<>), typeof(Repository<>)).AsTransient();
        _injector = ib.Build();
        _ = _injector.Resolve<IRepository<Customer>>(); // warm the closed registration cache

        var sc = new ServiceCollection();
        sc.AddTransient(typeof(IRepository<>), typeof(Repository<>));
        _medi = sc.BuildServiceProvider();
        _ = _medi.GetRequiredService<IRepository<Customer>>();

        var ab = new Autofac.ContainerBuilder();
        ab.RegisterGeneric(typeof(Repository<>)).As(typeof(IRepository<>)).InstancePerDependency();
        _autofac = ab.Build();
        _ = _autofac.Resolve<IRepository<Customer>>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _injector.Dispose();
        (_medi as IDisposable)?.Dispose();
        _autofac.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Injector")]
    public IRepository<Customer> Injector() => _injector.Resolve<IRepository<Customer>>();

    [Benchmark(Description = "MEDI")]
    public IRepository<Customer> Medi() => _medi.GetRequiredService<IRepository<Customer>>();

    [Benchmark(Description = "Autofac")]
    public IRepository<Customer> Autofac() => _autofac.Resolve<IRepository<Customer>>();
}
