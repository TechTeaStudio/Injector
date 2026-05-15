using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class ResolutionEngineTests
{
    public interface ILogger { }
    public class ConsoleLogger : ILogger { }
    public interface IService { ILogger Logger { get; } }
    public class Service : IService
    {
        public ILogger Logger { get; }
        public Service(ILogger logger) { Logger = logger; }
    }

    public class ServiceWithMultipleCtors
    {
        public string Where { get; }
        public ServiceWithMultipleCtors() { Where = "default"; }
        public ServiceWithMultipleCtors(ILogger logger) { Where = "withLogger:" + (logger is not null); }
    }

    [Fact]
    public void ConstructorInjection_ResolvesDependency()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsSingleton();
        b.Register<IService, Service>();
        using var c = b.Build();
        var svc = c.Resolve<IService>();
        Assert.NotNull(svc.Logger);
        Assert.IsType<ConsoleLogger>(svc.Logger);
    }

    [Fact]
    public void GreediestResolvableCtor_IsPicked()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsSingleton();
        b.Register<ServiceWithMultipleCtors>();
        using var c = b.Build();
        Assert.Equal("withLogger:True", c.Resolve<ServiceWithMultipleCtors>().Where);
    }

    [Fact]
    public void GreediestResolvableCtor_FallsBackWhenDepMissing()
    {
        var b = new ContainerBuilder();
        b.Register<ServiceWithMultipleCtors>();
        using var c = b.Build();
        Assert.Equal("default", c.Resolve<ServiceWithMultipleCtors>().Where);
    }

    [Fact]
    public void UnregisteredService_ThrowsServiceNotRegistered()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<ILogger>());
    }

    [Fact]
    public void ResolveByType_NonGenericOverload()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>();
        using var c = b.Build();
        Assert.IsType<ConsoleLogger>(c.Resolve(typeof(ILogger)));
    }

    // Direct cycle: A depends on B, B depends on A.
    public interface IA { }
    public interface IB { }
    public class A : IA { public A(IB b) { _ = b; } }
    public class B : IB { public B(IA a) { _ = a; } }

    [Fact]
    public void DirectCircularDependency_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IA, A>();
        b.Register<IB, B>();
        using var c = b.Build();
        var ex = Assert.Throws<CircularDependencyException>(() => c.Resolve<IA>());
        Assert.NotEmpty(ex.Chain);
        Assert.Contains(typeof(IA), ex.Chain);
    }

    // Indirect cycle: X -> Y -> Z -> X
    public interface IX { }
    public interface IY { }
    public interface IZ { }
    public class X : IX { public X(IY y) { _ = y; } }
    public class Y : IY { public Y(IZ z) { _ = z; } }
    public class Z : IZ { public Z(IX x) { _ = x; } }

    [Fact]
    public void IndirectCircularDependency_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IX, X>();
        b.Register<IY, Y>();
        b.Register<IZ, Z>();
        using var c = b.Build();
        var ex = Assert.Throws<CircularDependencyException>(() => c.Resolve<IX>());
        // Chain includes the repeated start (X -> Y -> Z -> X) so callers can see the closing edge.
        Assert.True(ex.Chain.Count >= 3, $"Expected at least 3 chain entries, got {ex.Chain.Count}");
        Assert.Contains(typeof(IX), ex.Chain);
        Assert.Contains(typeof(IY), ex.Chain);
        Assert.Contains(typeof(IZ), ex.Chain);
    }

    [Fact]
    public void CompiledActivator_IsCached_AcrossResolves()
    {
        // We can't directly observe the cache, but a second resolve must succeed cheaply.
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsSingleton();
        b.Register<IService, Service>();
        using var c = b.Build();
        var first = c.Resolve<IService>();
        var second = c.Resolve<IService>();
        Assert.NotSame(first, second); // transient
        Assert.Same(first.Logger, second.Logger); // singleton dep shared
    }

    [Fact]
    public void ScopedFromRoot_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsScoped();
        using var c = b.Build();
        Assert.Throws<InjectorException>(() => c.Resolve<ILogger>());
    }
}
