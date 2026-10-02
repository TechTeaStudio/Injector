using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

/// <summary>
/// Open-generic decorator coverage. The first group assembles the registration list directly via
/// the internal <see cref="Registration"/> / <see cref="Container"/> API (visible through
/// InternalsVisibleTo) to exercise the decorator subsystem in isolation; the <c>PublicApi_*</c>
/// group goes through <c>RegisterDecorator(Type, Type)</c> on the public builder, including
/// build-time validation.
/// </summary>
public class OpenGenericDecoratorsTests
{
    public interface IRepository<T> { string Trace(); }

    public class Repository<T> : IRepository<T>
    {
        public string Trace() => "repo<" + typeof(T).Name + ">";
    }

    public class LoggingRepository<T> : IRepository<T>
    {
        private readonly IRepository<T> _inner;
        public LoggingRepository(IRepository<T> inner) { _inner = inner; }
        public string Trace() => "log(" + _inner.Trace() + ")";
    }

    public class AuditRepository<T> : IRepository<T>
    {
        private readonly IRepository<T> _inner;
        public AuditRepository(IRepository<T> inner) { _inner = inner; }
        public string Trace() => "audit(" + _inner.Trace() + ")";
    }

    public class User { }
    public class Order { }

    // A decorator declared against the *closed* IRepository<User> specifically.
    public class UserOnlyRepository : IRepository<User>
    {
        private readonly IRepository<User> _inner;
        public UserOnlyRepository(IRepository<User> inner) { _inner = inner; }
        public string Trace() => "userOnly(" + _inner.Trace() + ")";
    }

    private static int _order;
    private static Registration OpenBase()
        => Registration.ForType(typeof(IRepository<>), typeof(Repository<>), Lifetime.Transient, _order++);
    private static Registration OpenDecorator(Type decorator)
        => Registration.ForDecorator(typeof(IRepository<>), decorator, _order++);
    private static Registration ClosedDecorator(Type service, Type decorator)
        => Registration.ForDecorator(service, decorator, _order++);

    [Fact]
    public void OpenGenericDecorator_WrapsClosedResolution()
    {
        _order = 0;
        var regs = new List<Registration>
        {
            OpenBase(),
            OpenDecorator(typeof(LoggingRepository<>)),
        };
        using var c = new Container(regs);

        Assert.Equal("log(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        Assert.IsType<LoggingRepository<User>>(c.Resolve<IRepository<User>>());
    }

    [Fact]
    public void OpenGenericDecorator_AppliesToMultipleClosedServices()
    {
        _order = 0;
        var regs = new List<Registration>
        {
            OpenBase(),
            OpenDecorator(typeof(LoggingRepository<>)),
        };
        using var c = new Container(regs);

        Assert.Equal("log(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        Assert.Equal("log(repo<Order>)", c.Resolve<IRepository<Order>>().Trace());
        Assert.IsType<LoggingRepository<User>>(c.Resolve<IRepository<User>>());
        Assert.IsType<LoggingRepository<Order>>(c.Resolve<IRepository<Order>>());
    }

    [Fact]
    public void OpenGenericDecorators_ChainInRegistrationOrder()
    {
        _order = 0;
        var regs = new List<Registration>
        {
            OpenBase(),
            OpenDecorator(typeof(LoggingRepository<>)), // registered first -> innermost decorator
            OpenDecorator(typeof(AuditRepository<>)),   // registered last  -> outermost decorator
        };
        using var c = new Container(regs);

        // Innermost-first application: repo -> log -> audit.
        Assert.Equal("audit(log(repo<User>))", c.Resolve<IRepository<User>>().Trace());
    }

    [Fact]
    public void OpenAndClosedDecorators_MergeByRegistrationOrder()
    {
        _order = 0;
        var regs = new List<Registration>
        {
            OpenBase(),
            OpenDecorator(typeof(LoggingRepository<>)),               // order 1
            ClosedDecorator(typeof(IRepository<User>), typeof(UserOnlyRepository)), // order 2
        };
        using var c = new Container(regs);

        // Both apply to IRepository<User>, ordered by declaration: log (open) then userOnly (closed).
        Assert.Equal("userOnly(log(repo<User>))", c.Resolve<IRepository<User>>().Trace());

        // Order<> only gets the open-generic decorator.
        Assert.Equal("log(repo<Order>)", c.Resolve<IRepository<Order>>().Trace());
    }

    [Fact]
    public void OpenGenericDecorator_CompiledActivatorReusedPerClosedType()
    {
        _order = 0;
        var regs = new List<Registration>
        {
            OpenBase(),
            OpenDecorator(typeof(LoggingRepository<>)),
        };
        using var c = new Container(regs);

        // Repeated resolves of the same closed type must keep producing the wrapped result
        // (i.e. the cached closed-decorator activator is reused, not recompiled wrongly).
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal("log(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        }
    }

    [Fact]
    public void ClosedDecorator_StillWorksWithoutOpenGeneric()
    {
        // Sanity: the existing closed-only path is unaffected by the new merge logic.
        _order = 0;
        var regs = new List<Registration>
        {
            Registration.ForType(typeof(IRepository<User>), typeof(Repository<User>), Lifetime.Transient, _order++),
            ClosedDecorator(typeof(IRepository<User>), typeof(UserOnlyRepository)),
        };
        using var c = new Container(regs);

        Assert.Equal("userOnly(repo<User>)", c.Resolve<IRepository<User>>().Trace());
    }

    // --- ValidateDecorators: closed decorator satisfied by an open-generic base (public builder). ---

    [Fact]
    public void ClosedDecorator_SatisfiedByOpenGenericBase_BuildsAndWraps()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        // Closed decorator on IRepository<User>; only an open-generic base is registered. Build-time
        // validation must accept this (the container closes the open base on demand) and the closed
        // decorator must wrap the closed resolution at runtime.
        b.RegisterDecorator<IRepository<User>, UserOnlyRepository>();
        using var c = b.Build();

        Assert.Equal("userOnly(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        // The decorator was declared only for User; Order resolves undecorated.
        Assert.Equal("repo<Order>", c.Resolve<IRepository<Order>>().Trace());
    }

    [Fact]
    public void ClosedDecorator_WithNoBaseAtAll_StillFailsValidation()
    {
        var b = new ContainerBuilder();
        // No base registration for IRepository<User> (closed or open) -> must throw on build.
        b.RegisterDecorator<IRepository<User>, UserOnlyRepository>();
        Assert.Throws<RegistrationException>(() => b.Build());
    }

    // --- Public API: RegisterDecorator(Type, Type) ------------------------------------------------

    [Fact]
    public void PublicApi_OpenGenericDecorator_OverOpenGenericBase_Wraps()
    {
        IContainerBuilder b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        b.RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>));
        using var c = b.Build();

        Assert.Equal("log(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        Assert.Equal("log(repo<Order>)", c.Resolve<IRepository<Order>>().Trace());
    }

    [Fact]
    public void PublicApi_OpenGenericDecorators_ChainInRegistrationOrder()
    {
        IContainerBuilder b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        b.RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>));
        b.RegisterDecorator(typeof(IRepository<>), typeof(AuditRepository<>));
        using var c = b.Build();

        Assert.Equal("audit(log(repo<User>))", c.Resolve<IRepository<User>>().Trace());
    }

    [Fact]
    public void PublicApi_OpenGenericDecorator_OverClosedOnlyBase_BuildsAndWraps()
    {
        IContainerBuilder b = new ContainerBuilder();
        // Only the closed IRepository<User> is registered; the decorator is declared on IRepository<>.
        b.Register<IRepository<User>, Repository<User>>();
        b.RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>));
        using var c = b.Build();

        Assert.Equal("log(repo<User>)", c.Resolve<IRepository<User>>().Trace());
        // No base for Order at all -> still unresolvable, the decorator does not conjure one.
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<IRepository<Order>>());
    }

    [Fact]
    public void PublicApi_OpenGenericDecorator_WithNoBaseAtAll_FailsValidation()
    {
        IContainerBuilder b = new ContainerBuilder();
        b.RegisterDecorator(typeof(IRepository<>), typeof(LoggingRepository<>));
        Assert.Throws<RegistrationException>(() => b.Build());
    }

    [Fact]
    public void PublicApi_ClosedDecorator_ByType_Wraps()
    {
        IContainerBuilder b = new ContainerBuilder();
        b.Register<IRepository<User>, Repository<User>>();
        b.RegisterDecorator(typeof(IRepository<User>), typeof(UserOnlyRepository));
        using var c = b.Build();

        Assert.Equal("userOnly(repo<User>)", c.Resolve<IRepository<User>>().Trace());
    }

    public class NotARepository { }
    public abstract class AbstractRepository<T> : IRepository<T>
    {
        public abstract string Trace();
    }

    [Fact]
    public void PublicApi_RegisterDecorator_ValidatesArguments()
    {
        IContainerBuilder b = new ContainerBuilder();
        Assert.Throws<ArgumentNullException>(() => b.RegisterDecorator(null!, typeof(LoggingRepository<>)));
        Assert.Throws<ArgumentNullException>(() => b.RegisterDecorator(typeof(IRepository<>), null!));
        // Mixed open/closed arity.
        Assert.Throws<RegistrationException>(() => b.RegisterDecorator(typeof(IRepository<>), typeof(UserOnlyRepository)));
        Assert.Throws<RegistrationException>(() => b.RegisterDecorator(typeof(IRepository<User>), typeof(LoggingRepository<>)));
        // Decorator that does not implement the service.
        Assert.Throws<RegistrationException>(() => b.RegisterDecorator(typeof(IRepository<User>), typeof(NotARepository)));
        Assert.Throws<RegistrationException>(() => b.RegisterDecorator(typeof(IRepository<>), typeof(NotARepository)));
        // Abstract decorator cannot be instantiated.
        Assert.Throws<RegistrationException>(() => b.RegisterDecorator(typeof(IRepository<>), typeof(AbstractRepository<>)));
    }
}
