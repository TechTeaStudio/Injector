using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Extensions.DependencyInjection;

namespace TechTeaStudio.Injector.Extensions.DependencyInjection.Tests;

public class HostFactoryTests
{
    public interface IGreeter { string Greet(); }
    public class Greeter : IGreeter { public string Greet() => "hello"; }

    public class Counter
    {
        public int Value { get; set; }
    }

    [Fact]
    public void Factory_CreateBuilder_ReturnsPopulatedContainerBuilder()
    {
        var factory = new InjectorServiceProviderFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Greeter>();

        IContainerBuilder cb = factory.CreateBuilder(services);

        Assert.NotNull(cb);
        IContainer container = cb.Build();
        var greeter = container.Resolve<IGreeter>();
        Assert.IsType<Greeter>(greeter);
    }

    [Fact]
    public void Factory_CreateServiceProvider_BuildsContainer()
    {
        var factory = new InjectorServiceProviderFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IGreeter, Greeter>();
        IContainerBuilder cb = factory.CreateBuilder(services);

        IServiceProvider provider = factory.CreateServiceProvider(cb);

        Assert.NotNull(provider);
        var greeter = provider.GetRequiredService<IGreeter>();
        Assert.IsType<Greeter>(greeter);
    }

    [Fact]
    public void GenericHost_UseServiceProviderFactory_BuildsAndResolves()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseServiceProviderFactory(new InjectorServiceProviderFactory())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IGreeter, Greeter>();
                services.AddScoped<Counter>();
            });

        using var host = hostBuilder.Build();

        var greeter = host.Services.GetRequiredService<IGreeter>();
        Assert.Equal("hello", greeter.Greet());
    }

    [Fact]
    public void GenericHost_ScopedServices_AreScopedPerCreateScope()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseServiceProviderFactory(new InjectorServiceProviderFactory())
            .ConfigureServices(services =>
            {
                services.AddScoped<Counter>();
            });

        using var host = hostBuilder.Build();

        Counter c1, c2;
        using (var scope1 = host.Services.CreateScope())
        {
            c1 = scope1.ServiceProvider.GetRequiredService<Counter>();
            Assert.Same(c1, scope1.ServiceProvider.GetRequiredService<Counter>());
        }
        using (var scope2 = host.Services.CreateScope())
        {
            c2 = scope2.ServiceProvider.GetRequiredService<Counter>();
        }

        Assert.NotSame(c1, c2);
    }

    [Fact]
    public void GenericHost_DisposesCleanly()
    {
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseServiceProviderFactory(new InjectorServiceProviderFactory())
            .ConfigureServices(services =>
            {
                services.AddSingleton<IGreeter, Greeter>();
            });

        var host = hostBuilder.Build();
        _ = host.Services.GetRequiredService<IGreeter>();

        // Should dispose without throwing.
        host.Dispose();
    }

    public interface IBroken { }
    public class BrokenWithMissingDep : IBroken
    {
        // Depends on a service nobody registered, so the registration is "broken" — it can be
        // declared but never resolved. ValidateOnBuild=true must surface this at host build time.
        public BrokenWithMissingDep(IGreeter neverRegistered) { _ = neverRegistered; }
    }

    [Fact]
    public void Factory_WithValidateOnBuild_Throws_ForBrokenRegistration()
    {
        var factory = new InjectorServiceProviderFactory(new BuildOptions { ValidateOnBuild = true });
        var services = new ServiceCollection();
        services.AddSingleton<IBroken, BrokenWithMissingDep>();
        var cb = factory.CreateBuilder(services);

        Assert.Throws<AggregateException>(() => factory.CreateServiceProvider(cb));
    }

    [Fact]
    public void Factory_WithDefaultOptions_DoesNotValidate_ForBrokenRegistration()
    {
        // Parameterless factory must NOT validate eagerly — broken registrations stay deferred.
        var factory = new InjectorServiceProviderFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IBroken, BrokenWithMissingDep>();
        var cb = factory.CreateBuilder(services);

        // Building does not throw.
        var provider = factory.CreateServiceProvider(cb);
        Assert.NotNull(provider);

        // Resolving the broken service is what surfaces the missing dep — at *resolve* time.
        // The MEDI adapter funnels the inner ServiceNotRegisteredException through GetService
        // (returning null) and GetRequiredService wraps that as InvalidOperationException.
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IBroken>());

        ((IDisposable)provider).Dispose();
    }

    [Fact]
    public void GenericHost_WithValidateOnBuild_FailsAtHostBuild()
    {
        // Mirrors the documented use-case: passing BuildOptions { ValidateOnBuild = true } to the
        // factory causes Host.Build() to throw if any registration is broken.
        var hostBuilder = Host.CreateDefaultBuilder()
            .UseServiceProviderFactory(new InjectorServiceProviderFactory(new BuildOptions { ValidateOnBuild = true }))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IBroken, BrokenWithMissingDep>();
            });

        Assert.ThrowsAny<Exception>(() => hostBuilder.Build());
    }
}
