using System.Reflection;
using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class DecoratorsModulesScanTests
{
    public interface IFoo { string Trace(); }
    public class Foo : IFoo { public string Trace() => "impl"; }
    public class FooDecorator1 : IFoo
    {
        private readonly IFoo _inner;
        public FooDecorator1(IFoo inner) { _inner = inner; }
        public string Trace() => "d1(" + _inner.Trace() + ")";
    }
    public class FooDecorator2 : IFoo
    {
        private readonly IFoo _inner;
        public FooDecorator2(IFoo inner) { _inner = inner; }
        public string Trace() => "d2(" + _inner.Trace() + ")";
    }

    [Fact]
    public void Decorators_AppliedInRegistrationOrder()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        b.RegisterDecorator<IFoo, FooDecorator1>();
        b.RegisterDecorator<IFoo, FooDecorator2>();
        using var c = b.Build();
        Assert.Equal("d2(d1(impl))", c.Resolve<IFoo>().Trace());
    }

    [Fact]
    public void NoDecorators_PassThrough()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>();
        using var c = b.Build();
        Assert.Equal("impl", c.Resolve<IFoo>().Trace());
    }

    // Property injection
    public interface ILog { string Tag { get; } }
    public class TestLog : ILog { public string Tag => "test"; }
    public class NeedsPropertyInjection
    {
        [Inject] public ILog Logger { get; set; } = null!;
    }

    [Fact]
    public void PropertyInjection_PopulatesAttributedProperty()
    {
        var b = new ContainerBuilder();
        b.Register<ILog, TestLog>().AsSingleton();
        b.Register<NeedsPropertyInjection>();
        using var c = b.Build();
        var inst = c.Resolve<NeedsPropertyInjection>();
        Assert.NotNull(inst.Logger);
        Assert.Equal("test", inst.Logger.Tag);
    }

    // Method injection
    public class NeedsMethodInjection
    {
        public ILog? CapturedLog { get; private set; }
        public string? CapturedExtra { get; private set; }

        [Inject]
        public void Init(ILog log)
        {
            CapturedLog = log;
            CapturedExtra = "init-called";
        }
    }

    [Fact]
    public void MethodInjection_InvokesAttributedMethod()
    {
        var b = new ContainerBuilder();
        b.Register<ILog, TestLog>().AsSingleton();
        b.Register<NeedsMethodInjection>();
        using var c = b.Build();
        var inst = c.Resolve<NeedsMethodInjection>();
        Assert.NotNull(inst.CapturedLog);
        Assert.Equal("init-called", inst.CapturedExtra);
    }

    // Non-public method injection (parity with non-public property injection).
    public class NeedsNonPublicMethodInjection
    {
        public ILog? CapturedLog { get; private set; }
        public bool NonPublicInitCalled { get; private set; }

        [Inject]
        private void Init(ILog log)
        {
            CapturedLog = log;
            NonPublicInitCalled = true;
        }
    }

    [Fact]
    public void MethodInjection_InvokesNonPublicAttributedMethod()
    {
        var b = new ContainerBuilder();
        b.Register<ILog, TestLog>().AsSingleton();
        b.Register<NeedsNonPublicMethodInjection>();
        using var c = b.Build();
        var inst = c.Resolve<NeedsNonPublicMethodInjection>();
        Assert.True(inst.NonPublicInitCalled);
        Assert.NotNull(inst.CapturedLog);
        Assert.Equal("test", inst.CapturedLog!.Tag);
    }

    // Modules
    public class FooModule : IModule
    {
        public int RegisterCount { get; private set; }
        public void Register(IContainerBuilder builder)
        {
            RegisterCount++;
            builder.Register<IFoo, Foo>().AsSingleton();
        }
    }

    [Fact]
    public void Module_RegisterIsCalledExactlyOnce()
    {
        var b = new ContainerBuilder();
        var mod = new FooModule();
        b.RegisterModule(mod);
        using var c = b.Build();
        Assert.Equal(1, mod.RegisterCount);
        Assert.NotNull(c.Resolve<IFoo>());
    }

    [Fact]
    public void Module_Null_Throws()
    {
        var b = new ContainerBuilder();
        Assert.Throws<ArgumentNullException>(() => b.RegisterModule(null!));
    }

    // Assembly scanning
    public interface IScanMarker { string Mark(); }
    public class ScanA : IScanMarker { public string Mark() => "A"; }
    public class ScanB : IScanMarker { public string Mark() => "B"; }
    public class SkippedNotMarker { }

    [Fact]
    public void RegisterAssembly_WithFilter_RegistersOnlyMatching()
    {
        var b = new ContainerBuilder();
        b.RegisterAssembly(typeof(DecoratorsModulesScanTests).Assembly,
            t => typeof(IScanMarker).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract,
            Lifetime.Transient);
        using var c = b.Build();
        var marks = c.ResolveAll<IScanMarker>().Select(m => m.Mark()).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "A", "B" }, marks);
    }

    [Fact]
    public void RegisterAssembly_RegistersAgainstSelfAndInterfaces()
    {
        var b = new ContainerBuilder();
        b.RegisterAssembly(typeof(DecoratorsModulesScanTests).Assembly,
            t => t == typeof(ScanA),
            Lifetime.Singleton);
        using var c = b.Build();
        Assert.NotNull(c.Resolve<ScanA>());
        Assert.NotNull(c.Resolve<IScanMarker>());
    }

    [Fact]
    public void RegisterAssembly_NullAssembly_Throws()
    {
        var b = new ContainerBuilder();
        Assert.Throws<ArgumentNullException>(() => b.RegisterAssembly(null!));
    }

    // --- Feature 8: compiled decorator caching ---
    [Fact]
    public void CompiledDecorator_IsReusedAcrossResolves()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, Foo>().AsTransient();
        b.RegisterDecorator<IFoo, FooDecorator1>();
        using var c = b.Build();

        // Two resolves: both succeed and produce equivalent wrapped output.
        // The point is that no new ConstructorInfo lookup is required after the first build.
        for (var i = 0; i < 100; i++)
        {
            var trace = c.Resolve<IFoo>().Trace();
            Assert.Equal("d1(impl)", trace);
        }
    }

    // --- Feature 9: build-time decorator validation ---
    public interface IUnregisteredService { void Foo(); }
    public class StraySvcDecorator : IUnregisteredService
    {
        private readonly IUnregisteredService _inner;
        public StraySvcDecorator(IUnregisteredService inner) { _inner = inner; }
        public void Foo() { _inner.Foo(); }
    }

    [Fact]
    public void DecoratorWithoutMatchingBase_ThrowsRegistrationExceptionOnBuild()
    {
        var b = new ContainerBuilder();
        b.RegisterDecorator<IUnregisteredService, StraySvcDecorator>();
        Assert.Throws<RegistrationException>(() => b.Build());
    }
}
