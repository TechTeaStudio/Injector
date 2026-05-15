using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class OwnedTests
{
    public interface IReportBuilder { string Build(); }
    public class ReportBuilder : IReportBuilder, IDisposable
    {
        public bool Disposed { get; private set; }
        public string Build() => "report";
        public void Dispose() { Disposed = true; }
    }

    public interface IFoo : IDisposable { bool Disposed { get; } }
    public class Foo : IFoo
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; }
    }

    public class AsyncFoo : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public void OwnedExposesValueAndDisposesInnerOnDispose()
    {
        var b = new ContainerBuilder();
        b.Register<IReportBuilder, ReportBuilder>().AsScoped();
        using var c = b.Build();
        using var s = c.CreateScope();

        ReportBuilder inner;
        using (var owned = s.Resolve<Owned<IReportBuilder>>())
        {
            Assert.NotNull(owned.Value);
            inner = (ReportBuilder)owned.Value;
            Assert.False(inner.Disposed);
        }
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void DisposingOwnedDoesNotAffectParentScope()
    {
        var b = new ContainerBuilder();
        b.Register<IReportBuilder, ReportBuilder>().AsScoped();
        b.Register<IFoo, Foo>().AsScoped();
        using var c = b.Build();
        using var s = c.CreateScope();

        var fooInParentScope = s.Resolve<IFoo>();
        using (var owned = s.Resolve<Owned<IReportBuilder>>())
        {
            _ = owned.Value;
        }
        Assert.False(fooInParentScope.Disposed);
    }

    [Fact]
    public async Task OwnedAsyncDisposeReleasesInner()
    {
        var b = new ContainerBuilder();
        b.Register<AsyncFoo>().AsScoped();
        await using var c = b.Build();
        await using var s = c.CreateScope();

        AsyncFoo inner;
        await using (var owned = s.Resolve<Owned<AsyncFoo>>())
        {
            inner = owned.Value;
        }
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void OwnedWorksInsideFunc()
    {
        var b = new ContainerBuilder();
        b.Register<IReportBuilder, ReportBuilder>().AsScoped();
        using var c = b.Build();
        using var s = c.CreateScope();

        var factory = s.Resolve<Func<Owned<IReportBuilder>>>();
        ReportBuilder inner;
        using (var owned = factory())
        {
            inner = (ReportBuilder)owned.Value;
        }
        Assert.True(inner.Disposed);
    }
}
