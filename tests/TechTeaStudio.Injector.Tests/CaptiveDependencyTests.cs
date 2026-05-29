using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class CaptiveDependencyTests
{
    // --- Service graph fixtures ---------------------------------------------------------------

    public interface IScopedSvc { }
    public class ScopedSvc : IScopedSvc { }

    public interface ITransientSvc { }
    public class TransientSvc : ITransientSvc { }

    public interface ISingletonLeaf { }
    public class SingletonLeaf : ISingletonLeaf { }

    // Singleton that directly captures a scoped service.
    public interface ICaptiveSingleton { }
    public class CaptiveSingleton : ICaptiveSingleton
    {
        public CaptiveSingleton(IScopedSvc scoped) { _ = scoped; }
    }

    // Singleton -> transient -> scoped (transitive capture through a transient hop).
    public interface IMidTransient { }
    public class MidTransient : IMidTransient
    {
        public MidTransient(IScopedSvc scoped) { _ = scoped; }
    }
    public interface ITransitiveSingleton { }
    public class TransitiveSingleton : ITransitiveSingleton
    {
        public TransitiveSingleton(IMidTransient mid) { _ = mid; }
    }

    // Singleton -> singleton -> scoped (transitive capture through a singleton hop).
    public interface IMidSingleton { }
    public class MidSingleton : IMidSingleton
    {
        public MidSingleton(IScopedSvc scoped) { _ = scoped; }
    }
    public interface ISingletonChainRoot { }
    public class SingletonChainRoot : ISingletonChainRoot
    {
        public SingletonChainRoot(IMidSingleton mid) { _ = mid; }
    }

    // Singleton depending only on a transient (must NOT be flagged).
    public interface ISingletonNeedsTransient { }
    public class SingletonNeedsTransient : ISingletonNeedsTransient
    {
        public SingletonNeedsTransient(ITransientSvc transient) { _ = transient; }
    }

    // Singleton depending only on another singleton (healthy).
    public interface ISingletonNeedsSingleton { }
    public class SingletonNeedsSingleton : ISingletonNeedsSingleton
    {
        public SingletonNeedsSingleton(ISingletonLeaf leaf) { _ = leaf; }
    }

    // Scoped depending on scoped (healthy — both are short-lived).
    public interface IScopedNeedsScoped { }
    public class ScopedNeedsScoped : IScopedNeedsScoped
    {
        public ScopedNeedsScoped(IScopedSvc scoped) { _ = scoped; }
    }

    // --- Tests --------------------------------------------------------------------------------

    [Fact]
    public void ValidateScopes_True_SingletonDirectlyCapturesScoped_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<ICaptiveSingleton, CaptiveSingleton>().AsSingleton();

        var ex = Assert.Throws<AggregateException>(() => b.Build(new BuildOptions { ValidateScopes = true }));

        Assert.Single(ex.InnerExceptions);
        Assert.IsType<CaptiveDependencyException>(ex.InnerExceptions[0]);
        // Message names both the captor singleton and the captured scoped service.
        Assert.Contains(nameof(CaptiveSingleton), ex.Message);
        Assert.Contains(nameof(ScopedSvc), ex.Message);
    }

    [Fact]
    public void ValidateScopes_True_SingletonTransitivelyCapturesScopedViaTransient_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<IMidTransient, MidTransient>().AsTransient();
        b.Register<ITransitiveSingleton, TransitiveSingleton>().AsSingleton();

        var ex = Assert.Throws<AggregateException>(() => b.Build(new BuildOptions { ValidateScopes = true }));

        Assert.IsType<CaptiveDependencyException>(ex.InnerExceptions[0]);
        Assert.Contains(nameof(TransitiveSingleton), ex.Message);
        Assert.Contains(nameof(ScopedSvc), ex.Message);
    }

    [Fact]
    public void ValidateScopes_True_SingletonTransitivelyCapturesScopedViaSingleton_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<IMidSingleton, MidSingleton>().AsSingleton();
        b.Register<ISingletonChainRoot, SingletonChainRoot>().AsSingleton();

        var ex = Assert.Throws<AggregateException>(() => b.Build(new BuildOptions { ValidateScopes = true }));

        Assert.Contains(ex.InnerExceptions, e => e is CaptiveDependencyException);
        Assert.Contains(nameof(ScopedSvc), ex.Message);
    }

    [Fact]
    public void ValidateScopes_True_HealthyGraph_NoThrow()
    {
        var b = new ContainerBuilder();
        b.Register<ISingletonLeaf, SingletonLeaf>().AsSingleton();
        b.Register<ITransientSvc, TransientSvc>().AsTransient();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<ISingletonNeedsSingleton, SingletonNeedsSingleton>().AsSingleton();
        b.Register<IScopedNeedsScoped, ScopedNeedsScoped>().AsScoped();

        using var c = b.Build(new BuildOptions { ValidateScopes = true });
        Assert.NotNull(c);
    }

    [Fact]
    public void ValidateScopes_True_SingletonDependsOnTransient_NoThrow()
    {
        var b = new ContainerBuilder();
        b.Register<ITransientSvc, TransientSvc>().AsTransient();
        b.Register<ISingletonNeedsTransient, SingletonNeedsTransient>().AsSingleton();

        // Transient-into-singleton is tolerated (matches MEDI ValidateScopes); no false positive.
        using var c = b.Build(new BuildOptions { ValidateScopes = true });
        Assert.NotNull(c);
    }

    [Fact]
    public void ValidateScopes_False_SingletonCapturesScoped_NoThrow()
    {
        var b = new ContainerBuilder();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<ICaptiveSingleton, CaptiveSingleton>().AsSingleton();

        // Default behavior is preserved: no scope validation unless opted in.
        using var c = b.Build(new BuildOptions { ValidateScopes = false });
        Assert.NotNull(c);
    }

    [Fact]
    public void ValidateScopes_Default_SingletonCapturesScoped_NoThrow()
    {
        var b = new ContainerBuilder();
        b.Register<IScopedSvc, ScopedSvc>().AsScoped();
        b.Register<ICaptiveSingleton, CaptiveSingleton>().AsSingleton();

        using var c = b.Build(); // no options at all
        Assert.NotNull(c);
    }
}
