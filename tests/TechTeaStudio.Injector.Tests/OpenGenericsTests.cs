using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class OpenGenericsTests
{
    public interface IRepository<T> { Type Element { get; } }
    public class Repository<T> : IRepository<T> { public Type Element => typeof(T); }

    public class User { }
    public class Order { }

    public class UserRepository : IRepository<User> { public Type Element => typeof(User); }

    [Fact]
    public void ClosedGenericResolvesFromOpenGenericRegistration()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        using var c = b.Build();

        var repo = c.Resolve<IRepository<User>>();
        Assert.IsType<Repository<User>>(repo);
        Assert.Equal(typeof(User), repo.Element);
    }

    [Fact]
    public void OpenGenericRegistrationServesMultipleClosedGenerics()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        using var c = b.Build();

        var userRepo = c.Resolve<IRepository<User>>();
        var orderRepo = c.Resolve<IRepository<Order>>();
        Assert.IsType<Repository<User>>(userRepo);
        Assert.IsType<Repository<Order>>(orderRepo);
    }

    [Fact]
    public void OpenGenericScopedLifetimeIsPreservedWithinScope()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>)).AsScoped();
        using var c = b.Build();
        using var s = c.CreateScope();

        var a = s.Resolve<IRepository<User>>();
        var b1 = s.Resolve<IRepository<User>>();
        Assert.Same(a, b1);
    }

    [Fact]
    public void ClosedGenericRegistrationOverridesOpenGeneric()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>));
        b.Register<IRepository<User>, UserRepository>();
        using var c = b.Build();

        var repo = c.Resolve<IRepository<User>>();
        Assert.IsType<UserRepository>(repo);
    }

    [Fact]
    public void UnregisteredOpenGenericThrowsServiceNotRegistered()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<IRepository<User>>());
    }

    [Fact]
    public void RegisterOpenGeneric_SelfRegistration()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(Repository<>));
        using var c = b.Build();

        var repo = c.Resolve<Repository<User>>();
        Assert.IsType<Repository<User>>(repo);
    }
}
