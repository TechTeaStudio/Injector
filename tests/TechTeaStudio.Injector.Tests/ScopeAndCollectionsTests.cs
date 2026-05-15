using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class ScopeAndCollectionsTests
{
    public interface IUnitOfWork : IDisposable { bool Disposed { get; } }
    public class UnitOfWork : IUnitOfWork
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; }
    }

    public interface IHandler { string Name { get; } }
    public class HandlerA : IHandler { public string Name => "a"; }
    public class HandlerB : IHandler { public string Name => "b"; }
    public class HandlerC : IHandler { public string Name => "c"; }

    [Fact]
    public void Scoped_SameInstanceWithinScope_DifferentAcross()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsScoped();
        using var c = b.Build();

        using var s1 = c.CreateScope();
        var a = s1.Resolve<IUnitOfWork>();
        var b1 = s1.Resolve<IUnitOfWork>();
        Assert.Same(a, b1);

        using var s2 = c.CreateScope();
        var x = s2.Resolve<IUnitOfWork>();
        Assert.NotSame(a, x);
    }

    [Fact]
    public void DisposingScope_DisposesScopedDisposable()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsScoped();
        using var c = b.Build();

        IUnitOfWork captured;
        using (var s = c.CreateScope())
        {
            captured = s.Resolve<IUnitOfWork>();
            Assert.False(captured.Disposed);
        }
        Assert.True(captured.Disposed);
    }

    [Fact]
    public void DisposingScope_IsIdempotent()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsScoped();
        using var c = b.Build();

        var s = c.CreateScope();
        var uow = s.Resolve<IUnitOfWork>();
        s.Dispose();
        s.Dispose(); // must not throw
        Assert.True(uow.Disposed);
    }

    [Fact]
    public void ResolveFromDisposedScope_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsScoped();
        using var c = b.Build();

        var s = c.CreateScope();
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.Resolve<IUnitOfWork>());
    }

    [Fact]
    public void ResolveAll_ReturnsAllRegistrationsInOrder()
    {
        var b = new ContainerBuilder();
        b.Register<IHandler, HandlerA>().AsTransient();
        b.Register<IHandler, HandlerB>().AsTransient();
        b.Register<IHandler, HandlerC>().AsSingleton();
        using var c = b.Build();
        var names = c.ResolveAll<IHandler>().Select(h => h.Name).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, names);
    }

    [Fact]
    public void ResolveAll_EmptyWhenNoRegistrations()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        Assert.Empty(c.ResolveAll<IHandler>());
    }

    [Fact]
    public void ResolveIEnumerable_DelegatesToResolveAll()
    {
        var b = new ContainerBuilder();
        b.Register<IHandler, HandlerA>();
        b.Register<IHandler, HandlerB>();
        using var c = b.Build();
        var all = c.Resolve<IEnumerable<IHandler>>();
        Assert.Equal(2, all.Count());
    }

    [Fact]
    public void ResolveDictionary_OnlyIncludesKeyedRegistrations()
    {
        var b = new ContainerBuilder();
        b.Register<IHandler, HandlerA>().WithKey("alpha").AsTransient();
        b.Register<IHandler, HandlerB>().WithKey("bravo").AsTransient();
        b.Register<IHandler, HandlerC>().AsTransient(); // no key
        using var c = b.Build();
        var dict = c.ResolveDictionary<string, IHandler>();
        Assert.Equal(2, dict.Count);
        Assert.IsType<HandlerA>(dict["alpha"]);
        Assert.IsType<HandlerB>(dict["bravo"]);
    }

    [Fact]
    public void ResolveIDictionary_DelegatesToResolveDictionary()
    {
        var b = new ContainerBuilder();
        b.Register<IHandler, HandlerA>().WithKey("alpha");
        b.Register<IHandler, HandlerB>().WithKey("bravo");
        using var c = b.Build();
        var dict = c.Resolve<IDictionary<string, IHandler>>();
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public void TransientDisposable_InScope_DisposedWithScope()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsTransient();
        using var c = b.Build();

        IUnitOfWork? captured;
        using (var s = c.CreateScope())
        {
            captured = s.Resolve<IUnitOfWork>();
        }
        Assert.True(captured.Disposed);
    }

    [Fact]
    public void ContainerDispose_DisposesSingletonDisposables()
    {
        var b = new ContainerBuilder();
        b.Register<IUnitOfWork, UnitOfWork>().AsSingleton();
        var c = b.Build();
        var uow = c.Resolve<IUnitOfWork>();
        c.Dispose();
        Assert.True(uow.Disposed);
    }
}
