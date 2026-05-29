using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class KeyedAndOptionalResolutionTests
{
    public interface IGreeter { string Greet(); }
    public class English : IGreeter { public string Greet() => "hello"; }
    public class French : IGreeter { public string Greet() => "bonjour"; }

    public interface IUnused { }

    // A service that is registered, but whose construction fails because a transitive
    // dependency was never registered. Used to prove TryResolve does not swallow real failures.
    public interface INeedsDep { }
    public interface IMissingDep { }
    public class NeedsDep : INeedsDep
    {
        public NeedsDep(IMissingDep dep) { _ = dep; }
    }

    // --- Part A: public keyed resolution ---

    [Fact]
    public void ResolveKeyed_Generic_ReturnsKeyedRegistration()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        b.Register<IGreeter, French>().WithKey("fr");
        using var c = b.Build();

        Assert.Equal("hello", c.ResolveKeyed<IGreeter>("en").Greet());
        Assert.Equal("bonjour", c.ResolveKeyed<IGreeter>("fr").Greet());
    }

    [Fact]
    public void ResolveKeyed_ByType_ReturnsKeyedRegistration()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        var resolved = c.ResolveKeyed(typeof(IGreeter), "en");
        Assert.IsType<English>(resolved);
    }

    [Fact]
    public void ResolveKeyed_UnknownKey_Throws()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        Assert.Throws<ServiceNotRegisteredException>(() => c.ResolveKeyed<IGreeter>("nope"));
    }

    [Fact]
    public void ResolveKeyed_RespectsSingletonLifetime()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en").AsSingleton();
        using var c = b.Build();

        Assert.Same(c.ResolveKeyed<IGreeter>("en"), c.ResolveKeyed<IGreeter>("en"));
    }

    [Fact]
    public void TryResolveKeyed_Hit_ReturnsTrueWithValue()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        Assert.True(c.TryResolveKeyed<IGreeter>("en", out var value));
        Assert.Equal("hello", value.Greet());
    }

    [Fact]
    public void TryResolveKeyed_Miss_ReturnsFalse()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        Assert.False(c.TryResolveKeyed<IGreeter>("missing", out var value));
        Assert.Null(value);
    }

    [Fact]
    public void ResolveKeyed_FromScope_Works()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en").AsScoped();
        using var c = b.Build();
        using var scope = c.CreateScope();

        Assert.Equal("hello", scope.ResolveKeyed<IGreeter>("en").Greet());
        Assert.Same(scope.ResolveKeyed<IGreeter>("en"), scope.ResolveKeyed<IGreeter>("en"));
    }

    [Fact]
    public void TryResolveKeyed_FromScope_Miss_ReturnsFalse()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();
        using var scope = c.CreateScope();

        Assert.False(scope.TryResolveKeyed<IGreeter>("missing", out _));
    }

    // --- Part B: TryResolve / ResolveOptional ---

    [Fact]
    public void TryResolve_Registered_ReturnsTrueWithValue()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();

        Assert.True(c.TryResolve<IGreeter>(out var value));
        Assert.IsType<English>(value);
    }

    [Fact]
    public void TryResolve_Unregistered_ReturnsFalse()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.False(c.TryResolve<IUnused>(out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_ByType_Unregistered_ReturnsFalseWithNull()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.False(c.TryResolve(typeof(IUnused), out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryResolve_RegisteredButBrokenDependency_StillThrows()
    {
        var b = new ContainerBuilder();
        b.Register<INeedsDep, NeedsDep>(); // IMissingDep is never registered.
        using var c = b.Build();

        // The top-level type IS registered, so the genuine downstream failure must propagate
        // rather than being swallowed into a false return.
        Assert.Throws<ServiceNotRegisteredException>(() => c.TryResolve<INeedsDep>(out _));
    }

    [Fact]
    public void ResolveOptional_Registered_ReturnsInstance()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();

        var value = c.ResolveOptional<IGreeter>();
        Assert.IsType<English>(value);
    }

    [Fact]
    public void ResolveOptional_Unregistered_ReturnsNull()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.Null(c.ResolveOptional<IUnused>());
    }

    [Fact]
    public void ResolveOptional_ByType_Unregistered_ReturnsNull()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.Null(c.ResolveOptional(typeof(IUnused)));
    }

    [Fact]
    public void ResolveOptional_RegisteredButBrokenDependency_StillThrows()
    {
        var b = new ContainerBuilder();
        b.Register<INeedsDep, NeedsDep>();
        using var c = b.Build();

        Assert.Throws<ServiceNotRegisteredException>(() => c.ResolveOptional<INeedsDep>());
    }

    [Fact]
    public void TryResolve_FromScope_ScopedService_ReturnsTrue()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().AsScoped();
        using var c = b.Build();
        using var scope = c.CreateScope();

        Assert.True(scope.TryResolve<IGreeter>(out var value));
        Assert.IsType<English>(value);
    }

    [Fact]
    public void TryResolve_FromScope_ChildScopeExtraRegistration_ReturnsTrue()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register<IGreeter, French>());

        Assert.True(child.TryResolve<IGreeter>(out var value));
        Assert.IsType<French>(value);
    }

    [Fact]
    public void ResolveOptional_FromScope_Unregistered_ReturnsNull()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var scope = c.CreateScope();

        Assert.Null(scope.ResolveOptional<IUnused>());
    }

    [Fact]
    public void TryResolve_CollectionType_AlwaysResolvesEvenWhenEmpty()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        // IEnumerable<T> resolves to an empty sequence; it is never "unregistered".
        Assert.True(c.TryResolve<IEnumerable<IGreeter>>(out var value));
        Assert.NotNull(value);
        Assert.Empty(value);
    }

    // --- Part C: IsRegistered ---

    [Fact]
    public void IsRegistered_Generic_TrueWhenRegistered()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();

        Assert.True(c.IsRegistered<IGreeter>());
        Assert.False(c.IsRegistered<IUnused>());
    }

    [Fact]
    public void IsRegistered_ByType_TrueWhenRegistered()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();

        Assert.True(c.IsRegistered(typeof(IGreeter)));
        Assert.False(c.IsRegistered(typeof(IUnused)));
    }

    [Fact]
    public void IsRegistered_KeyedRegistration_IsReported()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        // A keyed registration is still a registration of the service type.
        Assert.True(c.IsRegistered<IGreeter>());
    }
}
