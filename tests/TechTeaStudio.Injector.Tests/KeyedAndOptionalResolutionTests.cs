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
    public void IsRegistered_KeyedOnlyRegistration_IsNotReportedAsUnkeyed()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        // A keyed registration is reachable only through ResolveKeyed / ResolveDictionary, so the
        // unkeyed probe answers the same way an unkeyed Resolve / TryResolve would.
        Assert.False(c.IsRegistered<IGreeter>());
        Assert.False(c.TryResolve<IGreeter>(out _));
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<IGreeter>());
    }

    // --- Part D: keyed registrations are invisible to unkeyed resolution ---

    [Fact]
    public void Resolve_Unkeyed_SkipsKeyedRegistrationEvenWhenRegisteredLast()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        b.Register<IGreeter, French>().WithKey("fr");
        using var c = b.Build();

        Assert.IsType<English>(c.Resolve<IGreeter>());
        Assert.IsType<French>(c.ResolveKeyed<IGreeter>("fr"));
    }

    [Fact]
    public void ResolveAll_ExcludesKeyedRegistrations()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        b.Register<IGreeter, French>().WithKey("fr");
        using var c = b.Build();

        var all = c.ResolveAll<IGreeter>().ToArray();
        Assert.Single(all);
        Assert.IsType<English>(all[0]);
        Assert.Single(c.Resolve<IEnumerable<IGreeter>>());

        // The keyed one is still reachable through the dictionary.
        var dict = c.ResolveDictionary<string, IGreeter>();
        Assert.Single(dict);
        Assert.IsType<French>(dict["fr"]);
    }

    [Fact]
    public void ResolveAll_InChildScope_ExcludesKeyedExtraRegistrations()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register<IGreeter, French>().WithKey("fr"));

        Assert.Single(child.ResolveAll<IGreeter>());
        // A keyed child registration must not shadow the unkeyed parent one either.
        Assert.IsType<English>(child.Resolve<IGreeter>());
    }

    public class NeedsGreeter
    {
        public IGreeter Greeter { get; }
        public NeedsGreeter(IGreeter greeter) { Greeter = greeter; }
    }

    [Fact]
    public void ConstructorInjection_Unkeyed_SkipsKeyedRegistration()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        b.Register<IGreeter, French>().WithKey("fr");
        b.Register<NeedsGreeter>();
        using var c = b.Build();

        Assert.IsType<English>(c.Resolve<NeedsGreeter>().Greeter);
    }

    public interface IRepository<T> { string Name { get; } }
    public class Repository<T> : IRepository<T> { public string Name => "plain"; }
    public class CachedRepository<T> : IRepository<T> { public string Name => "cached"; }
    public class User { }

    // --- Part E: keyed resolution through open generics and child scopes ---

    [Fact]
    public void ResolveKeyed_OpenGenericRegistration_IsFound()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        b.Register(typeof(IRepository<>), typeof(CachedRepository<>)).WithKey("cached").AsSingleton();
        using var c = b.Build();

        Assert.Equal("cached", c.ResolveKeyed<IRepository<User>>("cached").Name);
        Assert.Same(c.ResolveKeyed<IRepository<User>>("cached"), c.ResolveKeyed<IRepository<User>>("cached"));
        Assert.True(c.TryResolveKeyed<IRepository<User>>("cached", out var viaTry));
        Assert.Equal("cached", viaTry.Name);
        // The unkeyed open generic is still the default.
        Assert.Equal("plain", c.Resolve<IRepository<User>>().Name);
        // And ResolveDictionary agrees with ResolveKeyed.
        Assert.Equal("cached", c.ResolveDictionary<string, IRepository<User>>()["cached"].Name);
    }

    [Fact]
    public void ResolveKeyed_ChildScopeExtraRegistration_IsFound()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register<IGreeter, French>().WithKey("fr"));

        Assert.Equal("bonjour", child.ResolveKeyed<IGreeter>("fr").Greet());
        Assert.True(child.TryResolveKeyed<IGreeter>("fr", out var viaTry));
        Assert.Equal("bonjour", viaTry.Greet());

        // The parent scope does not see the child's registration.
        Assert.False(scope.TryResolveKeyed<IGreeter>("fr", out _));
        Assert.Throws<ServiceNotRegisteredException>(() => scope.ResolveKeyed<IGreeter>("fr"));
    }

    [Fact]
    public void ResolveKeyed_ChildScopeRegistration_TakesPrecedenceOverContainer()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("k");
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register<IGreeter, French>().WithKey("k"));

        Assert.IsType<French>(child.ResolveKeyed<IGreeter>("k"));
        Assert.IsType<English>(scope.ResolveKeyed<IGreeter>("k"));
    }

    [Fact]
    public void ResolveKeyed_ChildScopeOpenGenericRegistration_IsFound()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register(typeof(IRepository<>), typeof(CachedRepository<>)).WithKey("cached"));

        Assert.Equal("cached", child.ResolveKeyed<IRepository<User>>("cached").Name);
    }

    [Fact]
    public void ResolveKeyed_UnknownKey_MessageNamesTheKey()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>().WithKey("en");
        using var c = b.Build();

        var ex = Assert.Throws<ServiceNotRegisteredException>(() => c.ResolveKeyed<IGreeter>("nope"));
        Assert.Contains("nope", ex.Message);
        Assert.Contains(typeof(IGreeter).FullName!, ex.Message);
        Assert.Equal("nope", ex.Key);
    }

    // --- Part F: TryResolve / ResolveOptional on synthetic wrappers ---

    [Fact]
    public void TryResolve_OwnedOfUnregistered_ReturnsFalse()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.False(c.TryResolve<Owned<IUnused>>(out var value));
        Assert.Null(value);
        Assert.Null(c.ResolveOptional<Owned<IUnused>>());
        Assert.False(c.IsRegistered<Owned<IUnused>>());
    }

    [Fact]
    public void TryResolve_LazyAndFuncOfUnregistered_ReturnFalse()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();

        Assert.False(c.TryResolve<Lazy<IUnused>>(out _));
        Assert.False(c.TryResolve<Func<IUnused>>(out _));
        Assert.Null(c.ResolveOptional<Lazy<IUnused>>());
        Assert.Null(c.ResolveOptional<Func<IUnused>>());
    }

    [Fact]
    public void TryResolve_WrappersOfRegistered_ReturnTrue()
    {
        var b = new ContainerBuilder();
        b.Register<IGreeter, English>();
        using var c = b.Build();

        Assert.True(c.TryResolve<Lazy<IGreeter>>(out var lazy));
        Assert.Equal("hello", lazy.Value.Greet());
        Assert.True(c.TryResolve<Func<IGreeter>>(out var func));
        Assert.Equal("hello", func().Greet());
        Assert.True(c.TryResolve<Owned<IGreeter>>(out var owned));
        using (owned) Assert.Equal("hello", owned.Value.Greet());
        // Func<P, T> is always synthesizable: the caller supplies the missing parameter.
        Assert.True(c.TryResolve<Func<string, IUnused>>(out _));
    }

    [Fact]
    public void TryResolve_WrapperOfRegisteredButBroken_StillThrowsOnConstruction()
    {
        var b = new ContainerBuilder();
        b.Register<INeedsDep, NeedsDep>(); // IMissingDep is never registered.
        using var c = b.Build();

        // Owned<T> constructs eagerly, so the genuine downstream failure must propagate.
        Assert.Throws<ServiceNotRegisteredException>(() => c.TryResolve<Owned<INeedsDep>>(out _));
        // Lazy<T> defers construction; TryResolve succeeds and the failure surfaces on .Value.
        Assert.True(c.TryResolve<Lazy<INeedsDep>>(out var lazy));
        Assert.Throws<ServiceNotRegisteredException>(() => lazy.Value);
    }

    [Fact]
    public void TryResolve_WrapperOfChildScopeRegistration_ReturnsTrue()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var scope = c.CreateScope();
        using var child = scope.BeginLifetimeScope(cb => cb.Register<IGreeter, French>());

        Assert.True(child.TryResolve<Owned<IGreeter>>(out var owned));
        using (owned) Assert.Equal("bonjour", owned.Value.Greet());
        Assert.False(scope.TryResolve<Owned<IGreeter>>(out _));
    }
}
