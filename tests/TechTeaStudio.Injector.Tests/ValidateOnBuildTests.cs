using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class ValidateOnBuildTests
{
    public interface IFoo { }
    public class Foo : IFoo { }
    public interface IBar { }
    public class BarNeedsMissing : IBar { public BarNeedsMissing(IFoo foo) { _ = foo; } }
    public class ScopedDep { public ScopedDep(IFoo foo) { _ = foo; } }

    [Fact]
    public void ValidateOnBuild_True_HealthyRegistrations_NoThrow()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsSingleton();
        b.Register<IBar, BarNeedsMissing>();
        using var c = b.Build(new BuildOptions { ValidateOnBuild = true });
        Assert.NotNull(c);
    }

    [Fact]
    public void ValidateOnBuild_True_MissingDependency_ThrowsAtBuild()
    {
        var b = new ContainerBuilder();
        b.Register<IBar, BarNeedsMissing>(); // missing IFoo
        var ex = Assert.ThrowsAny<Exception>(() => b.Build(new BuildOptions { ValidateOnBuild = true }));
        Assert.Contains("IFoo", ex.Message);
    }

    [Fact]
    public void ValidateOnBuild_True_CircularDependency_ThrowsAtBuild()
    {
        var b = new ContainerBuilder();
        b.Register<IA, A>();
        b.Register<IB, B>();
        Assert.ThrowsAny<Exception>(() => b.Build(new BuildOptions { ValidateOnBuild = true }));
    }

    [Fact]
    public void ValidateOnBuild_False_DoesNotThrow_OnBrokenReg()
    {
        var b = new ContainerBuilder();
        b.Register<IBar, BarNeedsMissing>();
        using var c = b.Build(new BuildOptions { ValidateOnBuild = false });
        // It throws only at Resolve time.
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<IBar>());
    }

    [Fact]
    public void ValidateOnBuild_Default_NoValidationOccurs()
    {
        var b = new ContainerBuilder();
        b.Register<IBar, BarNeedsMissing>();
        using var c = b.Build(); // no opts
        Assert.Throws<ServiceNotRegisteredException>(() => c.Resolve<IBar>());
    }

    [Fact]
    public void ValidateOnBuild_True_ScopedRegistration_IsResolvedInScope()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsSingleton();
        b.Register<ScopedDep>().AsScoped();
        // Should NOT throw: validator must use a scope.
        using var c = b.Build(new BuildOptions { ValidateOnBuild = true });
        Assert.NotNull(c);
    }

    public interface IA { }
    public interface IB { }
    public class A : IA { public A(IB b) { _ = b; } }
    public class B : IB { public B(IA a) { _ = a; } }
}
