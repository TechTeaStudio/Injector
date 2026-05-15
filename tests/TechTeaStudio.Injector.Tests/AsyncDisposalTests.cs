using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class AsyncDisposalTests
{
    public class AsyncOnly : IAsyncDisposable
    {
        public bool AsyncDisposed { get; private set; }
        public ValueTask DisposeAsync() { AsyncDisposed = true; return ValueTask.CompletedTask; }
    }

    public class BothDisposables : IDisposable, IAsyncDisposable
    {
        public bool SyncDisposed { get; private set; }
        public bool AsyncDisposed { get; private set; }
        public void Dispose() { SyncDisposed = true; }
        public ValueTask DisposeAsync() { AsyncDisposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task ScopeDisposeAsync_DisposesAsyncDisposableScopedInstance()
    {
        var b = new ContainerBuilder();
        b.Register<AsyncOnly>().AsScoped();
        await using var c = b.Build();

        AsyncOnly captured;
        await using (var s = c.CreateScope())
        {
            captured = s.Resolve<AsyncOnly>();
        }
        Assert.True(captured.AsyncDisposed);
    }

    [Fact]
    public async Task DisposeAsync_PrefersAsyncDisposeOverSync()
    {
        var b = new ContainerBuilder();
        b.Register<BothDisposables>().AsScoped();
        await using var c = b.Build();

        BothDisposables captured;
        await using (var s = c.CreateScope())
        {
            captured = s.Resolve<BothDisposables>();
        }
        Assert.True(captured.AsyncDisposed);
        Assert.False(captured.SyncDisposed);
    }

    [Fact]
    public void SyncDispose_OnAsyncOnly_StillRuns()
    {
        var b = new ContainerBuilder();
        b.Register<AsyncOnly>().AsScoped();
        using var c = b.Build();

        AsyncOnly captured;
        using (var s = c.CreateScope())
        {
            captured = s.Resolve<AsyncOnly>();
        }
        Assert.True(captured.AsyncDisposed);
    }

    [Fact]
    public async Task ContainerDisposeAsync_DisposesSingletonAsyncDisposable()
    {
        var b = new ContainerBuilder();
        b.Register<AsyncOnly>().AsSingleton();
        var c = b.Build();
        var instance = c.Resolve<AsyncOnly>();
        await c.DisposeAsync();
        Assert.True(instance.AsyncDisposed);
    }
}
