using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class LazyFuncTests
{
    public interface IFoo { int Id { get; } }

    public class Foo : IFoo
    {
        public static int Created;
        public int Id { get; }
        public Foo() { Id = System.Threading.Interlocked.Increment(ref Created); }
    }

    public class FooWithParam : IFoo
    {
        public int Id { get; }
        public string Tag { get; }
        public FooWithParam(string tag) { Tag = tag; Id = 0; }
    }

    public class FooWithParamAndDep : IFoo
    {
        public int Id { get; }
        public string Tag { get; }
        public ILogger Logger { get; }
        public FooWithParamAndDep(string tag, ILogger logger)
        {
            Tag = tag; Logger = logger; Id = 0;
        }
    }

    public interface ILogger { }
    public class ConsoleLogger : ILogger { }

    [Fact]
    public void Lazy_DefersResolutionUntilValueAccessed()
    {
        Foo.Created = 0;
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        using var c = b.Build();

        var lazy = c.Resolve<Lazy<IFoo>>();
        Assert.Equal(0, Foo.Created);
        var f = lazy.Value;
        Assert.NotNull(f);
        Assert.Equal(1, Foo.Created);
    }

    [Fact]
    public void Func_TransientCreatesNewInstanceEachInvocation()
    {
        Foo.Created = 0;
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        using var c = b.Build();

        var factory = c.Resolve<Func<IFoo>>();
        var a = factory();
        var b1 = factory();
        Assert.NotSame(a, b1);
        Assert.Equal(2, Foo.Created);
    }

    [Fact]
    public void Func_SingletonReturnsSameInstance()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsSingleton();
        using var c = b.Build();
        var factory = c.Resolve<Func<IFoo>>();
        Assert.Same(factory(), factory());
    }

    [Fact]
    public void FuncWithParam_PassesArgumentAsCtorParameter()
    {
        var b = new ContainerBuilder();
        // Register the concrete type so Func<string,IFoo> can construct it.
        b.Register<FooWithParam>();
        using var c = b.Build();

        var factory = c.Resolve<Func<string, FooWithParam>>();
        var instance = factory("hello");
        Assert.Equal("hello", instance.Tag);
    }

    [Fact]
    public void FuncWithParam_ResolvesRemainingCtorParamsFromContainer()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsSingleton();
        b.Register<FooWithParamAndDep>();
        using var c = b.Build();

        var factory = c.Resolve<Func<string, FooWithParamAndDep>>();
        var instance = factory("hi");
        Assert.Equal("hi", instance.Tag);
        Assert.NotNull(instance.Logger);
    }

    [Fact]
    public void LazyAsConstructorParameter_IsAutoSupplied()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        b.Register<NeedsLazy>();
        using var c = b.Build();
        var x = c.Resolve<NeedsLazy>();
        Assert.NotNull(x.LazyFoo);
        // Not yet accessed.
        Assert.False(x.LazyFoo.IsValueCreated);
        _ = x.LazyFoo.Value;
        Assert.True(x.LazyFoo.IsValueCreated);
    }

    [Fact]
    public void FuncAsConstructorParameter_IsAutoSupplied()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        b.Register<NeedsFunc>();
        using var c = b.Build();
        var x = c.Resolve<NeedsFunc>();
        var f1 = x.Factory();
        var f2 = x.Factory();
        Assert.NotSame(f1, f2);
    }

    public class NeedsLazy
    {
        public Lazy<IFoo> LazyFoo { get; }
        public NeedsLazy(Lazy<IFoo> lazyFoo) { LazyFoo = lazyFoo; }
    }

    public class NeedsFunc
    {
        public Func<IFoo> Factory { get; }
        public NeedsFunc(Func<IFoo> factory) { Factory = factory; }
    }
}
