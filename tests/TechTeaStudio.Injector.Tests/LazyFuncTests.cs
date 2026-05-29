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

    // --- Caching behavior (FIX 3): Func<T>/Lazy<T>/Func<P,T> activators are cached but must stay
    //     scope-correct. The activator factory is shared per type; the resolver is passed in per call. ---

    [Fact]
    public void Func_ScopedReturnsScopeCachedInstancePerScope()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsScoped();
        using var c = b.Build();

        using var s1 = c.CreateScope();
        var f1 = s1.Resolve<Func<IFoo>>();
        // A scoped service resolved through Func<T> returns the same scope-cached instance each call.
        Assert.Same(f1(), f1());
        Assert.Same(s1.Resolve<IFoo>(), f1());

        using var s2 = c.CreateScope();
        var f2 = s2.Resolve<Func<IFoo>>();
        // Different scope -> different cached instance (proves the cached factory is not baking a scope).
        Assert.NotSame(f1(), f2());
        Assert.Same(s2.Resolve<IFoo>(), f2());
    }

    [Fact]
    public void Lazy_ScopedResolvesScopeCachedInstance()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsScoped();
        using var c = b.Build();

        using var s1 = c.CreateScope();
        var lazy1 = s1.Resolve<Lazy<IFoo>>();
        Assert.Same(s1.Resolve<IFoo>(), lazy1.Value);

        using var s2 = c.CreateScope();
        var lazy2 = s2.Resolve<Lazy<IFoo>>();
        Assert.NotSame(lazy1.Value, lazy2.Value);
        Assert.Same(s2.Resolve<IFoo>(), lazy2.Value);
    }

    [Fact]
    public void FuncWithParam_CachedFactoryStillResolvesScopedDepFromScope()
    {
        var b = new ContainerBuilder();
        b.Register<ILogger, ConsoleLogger>().AsScoped();
        b.Register<FooWithParamAndDep>();
        using var c = b.Build();

        using var s1 = c.CreateScope();
        var factory1 = s1.Resolve<Func<string, FooWithParamAndDep>>();
        var i1 = factory1("a");
        Assert.Equal("a", i1.Tag);
        // The scoped logger dependency must come from s1's scope cache, not the root.
        Assert.Same(s1.Resolve<ILogger>(), i1.Logger);

        using var s2 = c.CreateScope();
        var factory2 = s2.Resolve<Func<string, FooWithParamAndDep>>();
        var i2 = factory2("b");
        Assert.Same(s2.Resolve<ILogger>(), i2.Logger);
        Assert.NotSame(i1.Logger, i2.Logger);
    }

    [Fact]
    public void FuncWithParam_RepeatedResolvesReuseCompiledActivator()
    {
        var b = new ContainerBuilder();
        b.Register<FooWithParam>();
        using var c = b.Build();

        // Resolving the same closed Func<P,T> many times must not recompile per call and must
        // keep producing correctly-bound delegates.
        for (var i = 0; i < 5; i++)
        {
            var factory = c.Resolve<Func<string, FooWithParam>>();
            Assert.Equal("v" + i, factory("v" + i).Tag);
        }
    }
}
