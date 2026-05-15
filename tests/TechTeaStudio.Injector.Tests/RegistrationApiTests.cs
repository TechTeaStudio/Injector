using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class RegistrationApiTests
{
    public interface IFoo { string Name { get; } }
    public class Foo : IFoo { public string Name => "foo"; }
    public class FooAlt : IFoo { public string Name => "foo-alt"; }
    public interface IBar { }
    public class Bar : IBar { }
    public class StandaloneType { public string Tag { get; set; } = "default"; }

    [Fact]
    public void RegisterServiceAndImplementation_ResolvesToImplementation()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>();
        using var c = builder.Build();
        var foo = c.Resolve<IFoo>();
        Assert.IsType<Foo>(foo);
    }

    [Fact]
    public void RegisterConcreteType_ResolvesToItself()
    {
        var builder = new ContainerBuilder();
        builder.Register<StandaloneType>();
        using var c = builder.Build();
        Assert.NotNull(c.Resolve<StandaloneType>());
    }

    [Fact]
    public void RegisterWithFactory_InvokesFactoryEachTransientResolve()
    {
        var builder = new ContainerBuilder();
        var calls = 0;
        builder.Register<IFoo>(_ => { calls++; return new Foo(); }).AsTransient();
        using var c = builder.Build();
        _ = c.Resolve<IFoo>();
        _ = c.Resolve<IFoo>();
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RegisterInstance_AlwaysReturnsSameInstanceAndIsSingleton()
    {
        var builder = new ContainerBuilder();
        var inst = new Foo();
        builder.RegisterInstance<IFoo>(inst);
        using var c = builder.Build();
        Assert.Same(inst, c.Resolve<IFoo>());
        Assert.Same(c.Resolve<IFoo>(), c.Resolve<IFoo>());
    }

    [Fact]
    public void AsTransient_GivesDistinctInstances()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsTransient();
        using var c = builder.Build();
        Assert.NotSame(c.Resolve<IFoo>(), c.Resolve<IFoo>());
    }

    [Fact]
    public void AsSingleton_GivesSameInstance()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var c = builder.Build();
        Assert.Same(c.Resolve<IFoo>(), c.Resolve<IFoo>());
    }

    [Fact]
    public void AsScoped_GivesSameWithinScopeDifferentAcrossScopes()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsScoped();
        using var c = builder.Build();

        using var s1 = c.CreateScope();
        Assert.Same(s1.Resolve<IFoo>(), s1.Resolve<IFoo>());

        using var s2 = c.CreateScope();
        Assert.NotSame(s1.Resolve<IFoo>(), s2.Resolve<IFoo>());
    }

    [Fact]
    public void As_RegistersAdditionalServiceContracts()
    {
        var builder = new ContainerBuilder();
        builder.Register<Bar>().As(typeof(IBar)).AsSingleton();
        using var c = builder.Build();
        Assert.Same(c.Resolve<Bar>(), c.Resolve<IBar>());
    }

    [Fact]
    public void WithKey_AttachesKeyForDictionaryResolution()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().WithKey("a").AsTransient();
        builder.Register<IFoo, FooAlt>().WithKey("b").AsTransient();
        using var c = builder.Build();
        var dict = c.ResolveDictionary<string, IFoo>();
        Assert.Equal(2, dict.Count);
        Assert.IsType<Foo>(dict["a"]);
        Assert.IsType<FooAlt>(dict["b"]);
    }

    [Fact]
    public void DuplicateKeyForSameServiceType_ThrowsOnBuild()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().WithKey("dup");
        builder.Register<IFoo, FooAlt>().WithKey("dup");
        Assert.Throws<RegistrationException>(() => builder.Build());
    }

    [Fact]
    public void Build_TwiceOnSameBuilder_Throws()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>();
        using var _ = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Register_AfterBuild_Throws()
    {
        var builder = new ContainerBuilder();
        using var _ = builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Register<IFoo, Foo>());
    }

    [Fact]
    public void RegisterInstance_WithNull_Throws()
    {
        var builder = new ContainerBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.RegisterInstance<IFoo>(null!));
    }

    [Fact]
    public void RegisterFactory_WithNull_Throws()
    {
        var builder = new ContainerBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.Register<IFoo>((Func<IResolver, IFoo>)null!));
    }
}
