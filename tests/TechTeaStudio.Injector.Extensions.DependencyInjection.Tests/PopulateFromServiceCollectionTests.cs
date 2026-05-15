using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection.Tests;

public class PopulateFromServiceCollectionTests
{
    public interface IFoo { string Tag { get; } }
    public class Foo : IFoo { public string Tag => "foo"; }
    public class FooAlt : IFoo { public string Tag => "foo-alt"; }

    public interface IBar { IFoo Foo { get; } }
    public class Bar : IBar
    {
        public IFoo Foo { get; }
        public Bar(IFoo foo) { Foo = foo; }
    }

    public class Counter { public int Value { get; set; } }

    [Fact]
    public void TransientByType_ResolvesNewInstanceEachTime()
    {
        var services = new ServiceCollection();
        services.AddTransient<IFoo, Foo>();

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var a = provider.GetRequiredService<IFoo>();
        var b = provider.GetRequiredService<IFoo>();

        Assert.NotSame(a, b);
        Assert.IsType<Foo>(a);
    }

    [Fact]
    public void SingletonByType_ResolvesSameInstance()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFoo, Foo>();

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var a = provider.GetRequiredService<IFoo>();
        var b = provider.GetRequiredService<IFoo>();

        Assert.Same(a, b);
    }

    [Fact]
    public void ScopedByType_ResolvesPerScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<IFoo, Foo>();

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        IFoo a1, a2, b1;
        using (var scope1 = provider.CreateScope())
        {
            a1 = scope1.ServiceProvider.GetRequiredService<IFoo>();
            a2 = scope1.ServiceProvider.GetRequiredService<IFoo>();
        }
        using (var scope2 = provider.CreateScope())
        {
            b1 = scope2.ServiceProvider.GetRequiredService<IFoo>();
        }

        Assert.Same(a1, a2);
        Assert.NotSame(a1, b1);
    }

    [Fact]
    public void SingletonByInstance_ReturnsExactInstance()
    {
        var instance = new Foo();
        var services = new ServiceCollection();
        services.AddSingleton<IFoo>(instance);

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var resolved = provider.GetRequiredService<IFoo>();

        Assert.Same(instance, resolved);
    }

    [Fact]
    public void SingletonByFactory_InvokesFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFoo>(sp => new FooAlt());

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var resolved = provider.GetRequiredService<IFoo>();

        Assert.IsType<FooAlt>(resolved);
        Assert.Equal("foo-alt", resolved.Tag);
    }

    [Fact]
    public void FactoryReceivesServiceProvider_AbleToResolveDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFoo, Foo>();
        services.AddSingleton<IBar>(sp => new Bar(sp.GetRequiredService<IFoo>()));

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var bar = provider.GetRequiredService<IBar>();

        Assert.NotNull(bar);
        Assert.NotNull(bar.Foo);
        Assert.IsType<Foo>(bar.Foo);
    }

    [Fact]
    public void ScopedByFactory_ResolvesPerScope()
    {
        var services = new ServiceCollection();
        services.AddScoped<Counter>(_ => new Counter { Value = 7 });

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        Counter c1, c2;
        using (var s1 = provider.CreateScope())
        {
            c1 = s1.ServiceProvider.GetRequiredService<Counter>();
            Assert.Same(c1, s1.ServiceProvider.GetRequiredService<Counter>());
        }
        using (var s2 = provider.CreateScope())
        {
            c2 = s2.ServiceProvider.GetRequiredService<Counter>();
        }

        Assert.NotSame(c1, c2);
        Assert.Equal(7, c1.Value);
        Assert.Equal(7, c2.Value);
    }

    [Fact]
    public void TransientByFactory_InvokesFactoryEachTime()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddTransient<IFoo>(_ => { calls++; return new Foo(); });

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        _ = provider.GetRequiredService<IFoo>();
        _ = provider.GetRequiredService<IFoo>();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void KeyedSingleton_ByType_ResolvedThroughKeyedServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IFoo, Foo>("alpha");
        services.AddKeyedSingleton<IFoo, FooAlt>("beta");

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var alpha = provider.GetRequiredKeyedService<IFoo>("alpha");
        var beta = provider.GetRequiredKeyedService<IFoo>("beta");

        Assert.IsType<Foo>(alpha);
        Assert.IsType<FooAlt>(beta);
    }

    [Fact]
    public void KeyedSingleton_ByInstance_ReturnsExactInstance()
    {
        var instance = new Foo();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IFoo>("only", instance);

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        var resolved = provider.GetRequiredKeyedService<IFoo>("only");

        Assert.Same(instance, resolved);
    }

    [Fact]
    public void KeyedTransient_ByFactory_InvokesFactoryEachResolve()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddKeyedTransient<IFoo>("x", (sp, key) => { calls++; return new Foo(); });

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        _ = provider.GetRequiredKeyedService<IFoo>("x");
        _ = provider.GetRequiredKeyedService<IFoo>("x");

        Assert.Equal(2, calls);
    }

    [Fact]
    public void KeyedScoped_ResolvesPerScope()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IFoo, Foo>("s");

        var builder = new ContainerBuilder();
        builder.PopulateFromServiceCollection(services);
        using var container = builder.Build();
        var provider = container.AsServiceProvider();

        IFoo a, b;
        using (var s1 = provider.CreateScope())
        {
            a = s1.ServiceProvider.GetRequiredKeyedService<IFoo>("s");
            Assert.Same(a, s1.ServiceProvider.GetRequiredKeyedService<IFoo>("s"));
        }
        using (var s2 = provider.CreateScope())
        {
            b = s2.ServiceProvider.GetRequiredKeyedService<IFoo>("s");
        }

        Assert.NotSame(a, b);
    }
}
