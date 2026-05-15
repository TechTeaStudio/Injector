using Microsoft.Extensions.DependencyInjection;
using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection.Tests;

public class AdapterTests
{
    public interface IFoo { string Name { get; } }
    public class Foo : IFoo { public string Name => "foo"; }
    public class FooAlpha : IFoo { public string Name => "alpha"; }
    public class FooBeta : IFoo { public string Name => "beta"; }

    public interface IBar { }
    public class Bar : IBar { }

    public class TrackableService : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    public class AsyncTrackableService : IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }
        public ValueTask DisposeAsync() { DisposedAsync = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void GetService_RegisteredType_ReturnsInstance()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var foo = provider.GetService(typeof(IFoo));

        Assert.NotNull(foo);
        Assert.IsType<Foo>(foo);
    }

    [Fact]
    public void GetService_UnregisteredType_ReturnsNull()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var bar = provider.GetService(typeof(IBar));

        Assert.Null(bar);
    }

    [Fact]
    public void GetRequiredService_RegisteredType_ReturnsInstance()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var foo = provider.GetRequiredService<IFoo>();

        Assert.IsType<Foo>(foo);
    }

    [Fact]
    public void GetRequiredService_UnregisteredType_Throws()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IBar>());
    }

    [Fact]
    public void Scope_DisposesScopedTransientServices()
    {
        var builder = new ContainerBuilder();
        builder.Register<TrackableService>().AsScoped();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        TrackableService? scoped;
        using (var scope = provider.CreateScope())
        {
            scoped = scope.ServiceProvider.GetRequiredService<TrackableService>();
            Assert.False(scoped.Disposed);
        }

        Assert.True(scoped.Disposed);
    }

    [Fact]
    public void IsService_ReturnsTrue_ForRegisteredType()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(IFoo)));
        Assert.False(isService.IsService(typeof(IBar)));
    }

    [Fact]
    public void IsService_ReturnsTrue_ForServiceProviderIntrinsics()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(IServiceProvider)));
        Assert.True(isService.IsService(typeof(IServiceScopeFactory)));
    }

    [Fact]
    public void GetKeyedService_RetrievesByKey()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, FooAlpha>().WithKey("alpha").AsSingleton();
        builder.Register<IFoo, FooBeta>().WithKey("beta").AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyed = (IKeyedServiceProvider)provider;
        var alpha = keyed.GetKeyedService(typeof(IFoo), "alpha") as IFoo;
        var beta = keyed.GetKeyedService(typeof(IFoo), "beta") as IFoo;

        Assert.NotNull(alpha);
        Assert.NotNull(beta);
        Assert.Equal("alpha", alpha!.Name);
        Assert.Equal("beta", beta!.Name);
    }

    [Fact]
    public void GetKeyedService_MissingKey_ReturnsNull()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, FooAlpha>().WithKey("alpha").AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyed = (IKeyedServiceProvider)provider;
        var missing = keyed.GetKeyedService(typeof(IFoo), "zzz");

        Assert.Null(missing);
    }

    [Fact]
    public void GetRequiredKeyedService_MissingKey_Throws()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, FooAlpha>().WithKey("alpha").AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyed = (IKeyedServiceProvider)provider;
        Assert.Throws<InvalidOperationException>(
            () => keyed.GetRequiredKeyedService(typeof(IFoo), "zzz"));
    }

    [Fact]
    public void ServiceProvider_ResolvesIServiceProvider_AsItself()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var resolved = provider.GetService(typeof(IServiceProvider));

        Assert.Same(provider, resolved);
    }

    [Fact]
    public void ServiceProvider_ResolvesIServiceScopeFactory()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var factory = provider.GetService(typeof(IServiceScopeFactory)) as IServiceScopeFactory;

        Assert.NotNull(factory);
        using var scope = factory!.CreateScope();
        Assert.NotNull(scope);
        Assert.NotNull(scope.ServiceProvider);
    }

    [Fact]
    public async Task ServiceProvider_FromFactory_DisposesContainerAsync()
    {
        // The InjectorServiceProviderFactory hands out an owning provider so the host can
        // dispose the container by disposing the provider. This mirrors the Generic Host contract.
        var services = new ServiceCollection();
        services.AddSingleton<AsyncTrackableService>();
        var factory = new InjectorServiceProviderFactory();
        var builder = factory.CreateBuilder(services);
        var provider = factory.CreateServiceProvider(builder);
        var svc = provider.GetRequiredService<AsyncTrackableService>();

        await ((IAsyncDisposable)provider).DisposeAsync();

        Assert.True(svc.DisposedAsync);
    }

    [Fact]
    public void ServiceProvider_AsServiceProvider_DoesNotOwnContainer()
    {
        // .AsServiceProvider() is a non-owning convenience adapter — disposing the wrapper
        // must NOT dispose the underlying container that the caller still holds.
        var builder = new ContainerBuilder();
        builder.Register<TrackableService>().AsSingleton();
        var container = builder.Build();
        var provider = container.AsServiceProvider();
        var svc = provider.GetRequiredService<TrackableService>();

        ((IDisposable)provider).Dispose();

        Assert.False(svc.Disposed);
        // Disposing the actual container does the work.
        container.Dispose();
        Assert.True(svc.Disposed);
    }

    [Fact]
    public void Scope_ServiceProvider_ResolvesIServiceProvider_AsItself()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        using var scope = provider.CreateScope();
        var scopedSelf = scope.ServiceProvider.GetService(typeof(IServiceProvider));

        Assert.Same(scope.ServiceProvider, scopedSelf);
    }

    [Fact]
    public void IsService_ReturnsTrue_ForEnumerableOfRegisteredType()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(IEnumerable<IFoo>)));
        Assert.True(isService.IsService(typeof(IReadOnlyCollection<IFoo>)));
        Assert.True(isService.IsService(typeof(IReadOnlyList<IFoo>)));
        Assert.True(isService.IsService(typeof(ICollection<IFoo>)));
        Assert.True(isService.IsService(typeof(IList<IFoo>)));
    }

    [Fact]
    public void IsService_ReturnsTrue_ForEnumerableEvenWhenElementUnregistered()
    {
        // Mirrors MEDI: IsService(IEnumerable<TFoo>) is true regardless of T's registration
        // status — the resolved enumerable is just empty when no T is registered.
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(IEnumerable<IBar>)));
    }

    [Fact]
    public void IsService_ReturnsTrue_ForLazyAndFuncAndOwnedOfRegisteredType()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(Lazy<IFoo>)));
        Assert.True(isService.IsService(typeof(Func<IFoo>)));
        Assert.True(isService.IsService(typeof(Owned<IFoo>)));
    }

    [Fact]
    public void IsService_ReturnsFalse_ForLazyAndFuncAndOwnedOfUnregisteredType()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.False(isService.IsService(typeof(Lazy<IBar>)));
        Assert.False(isService.IsService(typeof(Func<IBar>)));
        Assert.False(isService.IsService(typeof(Owned<IBar>)));
    }

    [Fact]
    public void IsService_ReturnsTrue_ForParameterizedFunc()
    {
        // Func<P, T> with explicit parameters is satisfied by the container's synthetic activator
        // — we cannot know at IsService time whether the user will supply a registered T, so
        // we return true (matching the container's IsResolvable rule).
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var isService = (IServiceProviderIsService)provider;

        Assert.True(isService.IsService(typeof(Func<string, IBar>)));
    }

    [Fact]
    public void IsKeyedService_ReturnsTrue_ForRegisteredKey()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, FooAlpha>().WithKey("alpha").AsSingleton();
        builder.Register<IFoo, FooBeta>().WithKey("beta").AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyedIsService = (IServiceProviderIsKeyedService)provider;

        Assert.True(keyedIsService.IsKeyedService(typeof(IFoo), "alpha"));
        Assert.True(keyedIsService.IsKeyedService(typeof(IFoo), "beta"));
    }

    [Fact]
    public void IsKeyedService_ReturnsFalse_ForMissingKey()
    {
        var builder = new ContainerBuilder();
        builder.Register<IFoo, FooAlpha>().WithKey("alpha").AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyedIsService = (IServiceProviderIsKeyedService)provider;

        Assert.False(keyedIsService.IsKeyedService(typeof(IFoo), "missing"));
    }

    [Fact]
    public void IsKeyedService_NullKey_MatchesIsService()
    {
        // MEDI convention: IsKeyedService(t, null) behaves like IsService(t).
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyedIsService = (IServiceProviderIsKeyedService)provider;

        Assert.True(keyedIsService.IsKeyedService(typeof(IFoo), null));
        Assert.False(keyedIsService.IsKeyedService(typeof(IBar), null));
    }

    [Fact]
    public void IsKeyedService_DoesNotMatchUnkeyedRegistration()
    {
        // A plain (unkeyed) registration must NOT satisfy IsKeyedService(t, someKey).
        var builder = new ContainerBuilder();
        builder.Register<IFoo, Foo>().AsSingleton();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var keyedIsService = (IServiceProviderIsKeyedService)provider;

        Assert.False(keyedIsService.IsKeyedService(typeof(IFoo), "alpha"));
    }

    [Fact]
    public void ServiceProvider_ResolvesIServiceProviderIsKeyedService()
    {
        var builder = new ContainerBuilder();
        using var container = builder.Build();
        IServiceProvider provider = container.AsServiceProvider();

        var resolved = provider.GetService(typeof(IServiceProviderIsKeyedService));

        Assert.NotNull(resolved);
        Assert.Same(provider, resolved);
    }
}
