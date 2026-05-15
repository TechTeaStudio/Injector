using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class ChildScopeTests
{
    public interface IRequest { string Path { get; } }
    public class Request : IRequest
    {
        public string Path { get; }
        public Request(string path) { Path = path; }
    }

    public interface IHandler { IRequest Request { get; } }
    public class Handler : IHandler
    {
        public IRequest Request { get; }
        public Handler(IRequest req) { Request = req; }
    }

    public interface IFoo { string Tag { get; } }
    public class FooFromParent : IFoo { public string Tag => "parent"; }
    public class FooFromChild : IFoo { public string Tag => "child"; }

    public class DisposableThing : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; }
    }

    [Fact]
    public void ChildScopeSeesParentRegistrations()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, FooFromParent>().AsScoped();
        using var c = b.Build();
        using var parent = c.CreateScope();
        using var child = parent.BeginLifetimeScope(cb => { /* nothing extra */ });

        var foo = child.Resolve<IFoo>();
        Assert.Equal("parent", foo.Tag);
    }

    [Fact]
    public void ChildScopeRegistrationOverridesParent()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, FooFromParent>().AsScoped();
        using var c = b.Build();
        using var parent = c.CreateScope();
        using var child = parent.BeginLifetimeScope(cb => cb.Register<IFoo, FooFromChild>().AsTransient());

        var fooChild = child.Resolve<IFoo>();
        Assert.Equal("child", fooChild.Tag);

        var fooParent = parent.Resolve<IFoo>();
        Assert.Equal("parent", fooParent.Tag);
    }

    [Fact]
    public void ChildScopeRegisterInstance_AvailableInChildOnly()
    {
        var b = new ContainerBuilder();
        b.Register<IHandler, Handler>().AsTransient();
        using var c = b.Build();
        using var parent = c.CreateScope();
        var request = new Request("/users");
        using var child = parent.BeginLifetimeScope(cb => cb.RegisterInstance<IRequest>(request));

        var handler = child.Resolve<IHandler>();
        Assert.Same(request, handler.Request);
    }

    [Fact]
    public void DisposingChildDoesNotDisposeParent()
    {
        var b = new ContainerBuilder();
        b.Register<DisposableThing>().AsScoped();
        using var c = b.Build();
        using var parent = c.CreateScope();

        var inParent = parent.Resolve<DisposableThing>();
        var child = parent.BeginLifetimeScope(_ => { });
        child.Dispose();
        Assert.False(inParent.Disposed);
    }

    [Fact]
    public void NestedChildScopes_WorkRecursively()
    {
        var b = new ContainerBuilder();
        b.Register<IFoo, FooFromParent>().AsScoped();
        using var c = b.Build();
        using var s = c.CreateScope();
        using var s1 = s.BeginLifetimeScope(cb => cb.Register<IFoo, FooFromChild>());
        using var s2 = s1.BeginLifetimeScope(cb => { /* grandchild sees grandparent + parent */ });

        // s2 finds IFoo in s1 (the closer overriding parent) -> "child".
        Assert.Equal("child", s2.Resolve<IFoo>().Tag);
    }

    [Fact]
    public void ChildScopeOwnsItsOwnTransientDisposables()
    {
        var b = new ContainerBuilder();
        using var c = b.Build();
        using var parent = c.CreateScope();

        DisposableThing captured;
        using (var child = parent.BeginLifetimeScope(cb => cb.Register<DisposableThing>().AsTransient()))
        {
            captured = child.Resolve<DisposableThing>();
            Assert.False(captured.Disposed);
        }
        Assert.True(captured.Disposed);
    }
}
