using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

public class LifecycleHooksTests
{
    public interface ILog { string Tag { get; } }
    public class TestLog : ILog { public string Tag => "test"; }

    public class HasInjectableProperty
    {
        public List<string> Events { get; } = new();
        [Inject] public ILog Log { get; set; } = null!;
        public bool Configured { get; private set; }
        public bool Initialized { get; private set; }
        public void Configure() { Configured = true; Events.Add("configure-log=" + (Log is null)); }
        public void Initialize() { Initialized = true; Events.Add("initialize-log=" + (Log is not null)); }
    }

    public class Counter { public int Ctor; public int OnActivating; public int OnActivated; }
    public class Tracked
    {
        public static Counter Counter = new();
        public Tracked() { Counter.Ctor++; }
    }

    [Fact]
    public void OnActivating_RunsBeforePropertyInjection()
    {
        var b = new ContainerBuilder();
        b.Register<ILog, TestLog>().AsSingleton();
        b.Register<HasInjectableProperty>()
            .OnActivating(x => ((HasInjectableProperty)x).Configure());
        using var c = b.Build();

        var instance = c.Resolve<HasInjectableProperty>();
        Assert.True(instance.Configured);
        // OnActivating fires BEFORE injection, so Log should have been null at that point.
        Assert.Contains("configure-log=True", instance.Events);
        // Log is populated by the time we see it.
        Assert.NotNull(instance.Log);
    }

    [Fact]
    public void OnActivated_RunsAfterPropertyInjection()
    {
        var b = new ContainerBuilder();
        b.Register<ILog, TestLog>().AsSingleton();
        b.Register<HasInjectableProperty>()
            .OnActivated(x => ((HasInjectableProperty)x).Initialize());
        using var c = b.Build();

        var instance = c.Resolve<HasInjectableProperty>();
        Assert.True(instance.Initialized);
        Assert.Contains("initialize-log=True", instance.Events);
    }

    [Fact]
    public void Hooks_FireOnceForSingleton()
    {
        Tracked.Counter = new Counter();
        var b = new ContainerBuilder();
        b.Register<Tracked>().AsSingleton()
            .OnActivating(_ => Tracked.Counter.OnActivating++)
            .OnActivated(_ => Tracked.Counter.OnActivated++);
        using var c = b.Build();

        _ = c.Resolve<Tracked>();
        _ = c.Resolve<Tracked>();
        _ = c.Resolve<Tracked>();
        Assert.Equal(1, Tracked.Counter.Ctor);
        Assert.Equal(1, Tracked.Counter.OnActivating);
        Assert.Equal(1, Tracked.Counter.OnActivated);
    }

    [Fact]
    public void Hooks_FireEachTimeForTransient()
    {
        Tracked.Counter = new Counter();
        var b = new ContainerBuilder();
        b.Register<Tracked>().AsTransient()
            .OnActivating(_ => Tracked.Counter.OnActivating++)
            .OnActivated(_ => Tracked.Counter.OnActivated++);
        using var c = b.Build();

        _ = c.Resolve<Tracked>();
        _ = c.Resolve<Tracked>();
        _ = c.Resolve<Tracked>();
        Assert.Equal(3, Tracked.Counter.Ctor);
        Assert.Equal(3, Tracked.Counter.OnActivating);
        Assert.Equal(3, Tracked.Counter.OnActivated);
    }
}
