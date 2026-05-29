using Autofac;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Benchmarks;

/// <summary>
/// Container build/compile cost: register the same object graph and build the
/// container/provider. Captures the one-time setup price each container pays
/// before the first resolve (activator compilation, registration indexing).
/// The built instance is disposed each iteration to avoid leaking.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("ContainerBuild")]
public class ContainerBuildBenchmarks
{
    [Benchmark(Baseline = true, Description = "Injector")]
    public void Injector()
    {
        var ib = new ContainerBuilder();
        ib.Register<ILevel0, Level0>().AsTransient();
        ib.Register<ILevel1, Level1>().AsTransient();
        ib.Register<ILevel2, Level2>().AsTransient();
        ib.Register<ISingletonService, SingletonService>().AsSingleton();
        ib.Register<IScopedService, ScopedService>().AsScoped();
        ib.Register(typeof(IRepository<>), typeof(Repository<>)).AsTransient();
        using IContainer container = ib.Build();
    }

    [Benchmark(Description = "MEDI")]
    public void Medi()
    {
        var sc = new ServiceCollection();
        sc.AddTransient<ILevel0, Level0>();
        sc.AddTransient<ILevel1, Level1>();
        sc.AddTransient<ILevel2, Level2>();
        sc.AddSingleton<ISingletonService, SingletonService>();
        sc.AddScoped<IScopedService, ScopedService>();
        sc.AddTransient(typeof(IRepository<>), typeof(Repository<>));
        using ServiceProvider provider = sc.BuildServiceProvider();
    }

    [Benchmark(Description = "Autofac")]
    public void Autofac()
    {
        var ab = new Autofac.ContainerBuilder();
        ab.RegisterType<Level0>().As<ILevel0>().InstancePerDependency();
        ab.RegisterType<Level1>().As<ILevel1>().InstancePerDependency();
        ab.RegisterType<Level2>().As<ILevel2>().InstancePerDependency();
        ab.RegisterType<SingletonService>().As<ISingletonService>().SingleInstance();
        ab.RegisterType<ScopedService>().As<IScopedService>().InstancePerLifetimeScope();
        ab.RegisterGeneric(typeof(Repository<>)).As(typeof(IRepository<>)).InstancePerDependency();
        using Autofac.IContainer container = ab.Build();
    }
}
