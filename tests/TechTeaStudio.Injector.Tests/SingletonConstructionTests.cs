using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

/// <summary>
/// Singleton construction guarantees beyond the happy path covered by
/// <see cref="ConcurrencyCorrectnessTests"/>: a failed construction is retried on the next
/// resolve, same-thread singleton cycles surface as <see cref="CircularDependencyException"/>,
/// and a cross-thread singleton cycle is detected instead of deadlocking.
/// </summary>
public class SingletonConstructionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // --- A1: failed construction must not be cached ------------------------------------------

    public sealed class FlakySingleton
    {
        public static int Attempts;
        public static int FailFirst;

        public FlakySingleton()
        {
            var attempt = Interlocked.Increment(ref Attempts);
            if (attempt <= FailFirst) throw new InvalidOperationException("boom #" + attempt);
        }
    }

    [Fact]
    public void Singleton_FailedConstruction_IsRetriedOnNextResolve()
    {
        FlakySingleton.Attempts = 0;
        FlakySingleton.FailFirst = 1;

        var b = new ContainerBuilder();
        b.Register<FlakySingleton>().AsSingleton();
        using var c = b.Build();

        var first = Assert.Throws<InvalidOperationException>(() => c.Resolve<FlakySingleton>());
        Assert.Equal("boom #1", first.Message);

        // The second resolve must run the constructor again instead of replaying the cached failure.
        var instance = c.Resolve<FlakySingleton>();
        Assert.NotNull(instance);
        Assert.Equal(2, FlakySingleton.Attempts);
        Assert.Same(instance, c.Resolve<FlakySingleton>());
    }

    [Fact]
    public void Singleton_FactoryFailure_IsRetriedOnNextResolve()
    {
        var calls = 0;
        var b = new ContainerBuilder();
        b.Register<object>(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("first call fails");
            return new object();
        }).AsSingleton();
        using var c = b.Build();

        Assert.Throws<InvalidOperationException>(() => c.Resolve<object>());
        var a = c.Resolve<object>();
        Assert.Same(a, c.Resolve<object>());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Singleton_AfterFailure_StillConstructedOnceUnderContention()
    {
        FlakySingleton.Attempts = 0;
        FlakySingleton.FailFirst = 1;

        var b = new ContainerBuilder();
        b.Register<FlakySingleton>().AsSingleton();
        using var c = b.Build();

        Assert.Throws<InvalidOperationException>(() => c.Resolve<FlakySingleton>());

        const int workers = 64;
        var results = new FlakySingleton[workers];
        Parallel.For(0, workers, i => results[i] = c.Resolve<FlakySingleton>());

        // Exactly one successful construction after the failed one.
        Assert.Equal(2, FlakySingleton.Attempts);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    // Threads that are already waiting for an in-flight construction share the owner's failure
    // instead of silently retrying; the NEXT resolve constructs again.
    public static class FailingGate
    {
        public static ManualResetEventSlim? Release;
        public static ManualResetEventSlim? Started;
        public static int Attempts;
    }

    public sealed class FailingSlowSingleton
    {
        public FailingSlowSingleton()
        {
            Interlocked.Increment(ref FailingGate.Attempts);
            FailingGate.Started!.Set();
            FailingGate.Release!.Wait(Timeout);
            throw new InvalidOperationException("owner failed");
        }
    }

    [Fact]
    public void Singleton_WaiterReceivesOwnersException_ThenNextResolveRetries()
    {
        FailingGate.Release = new ManualResetEventSlim(false);
        FailingGate.Started = new ManualResetEventSlim(false);
        FailingGate.Attempts = 0;

        var b = new ContainerBuilder();
        b.Register<FailingSlowSingleton>().AsSingleton();
        using var c = b.Build();

        var owner = Task.Run(() => Record.Exception(() => c.Resolve<FailingSlowSingleton>()));
        Assert.True(FailingGate.Started.Wait(Timeout), "Owner constructor never started.");

        // Second resolver arrives while the owner is still inside the constructor and must wait.
        var waiter = Task.Run(() => Record.Exception(() => c.Resolve<FailingSlowSingleton>()));
        Assert.False(waiter.Wait(TimeSpan.FromMilliseconds(200)), "Waiter did not wait for the in-flight construction.");

        FailingGate.Release.Set();
        Assert.True(Task.WaitAll(new Task[] { owner, waiter }, Timeout), "Resolves did not complete.");

        Assert.IsType<InvalidOperationException>(owner.Result);
        Assert.IsType<InvalidOperationException>(waiter.Result);
        // One construction attempt served both; the waiter did not start a second one.
        Assert.Equal(1, FailingGate.Attempts);

        // A fresh resolve retries (and fails again, because this constructor always throws).
        Assert.Throws<InvalidOperationException>(() => c.Resolve<FailingSlowSingleton>());
        Assert.Equal(2, FailingGate.Attempts);
    }

    // --- A2: same-thread singleton cycle ------------------------------------------------------

    public interface ISingA { }
    public interface ISingB { }
    public class SingA : ISingA { public SingA(ISingB b) { _ = b; } }
    public class SingB : ISingB { public SingB(ISingA a) { _ = a; } }

    [Fact]
    public void SingletonCycle_SameThread_ThrowsCircularDependencyException()
    {
        var b = new ContainerBuilder();
        b.Register<ISingA, SingA>().AsSingleton();
        b.Register<ISingB, SingB>().AsSingleton();
        using var c = b.Build();

        var ex = Assert.Throws<CircularDependencyException>(() => c.Resolve<ISingA>());
        Assert.Contains(typeof(ISingA), ex.Chain);
        Assert.Contains(typeof(ISingB), ex.Chain);

        // The failed cycle must not poison the container: the same resolve keeps reporting the cycle.
        Assert.Throws<CircularDependencyException>(() => c.Resolve<ISingA>());
    }

    [Fact]
    public void SingletonCycle_SameThread_ViaFactories_ThrowsCircularDependencyException()
    {
        var b = new ContainerBuilder();
        b.Register<ISingA>(r => new SingA(r.Resolve<ISingB>())).AsSingleton();
        b.Register<ISingB>(r => new SingB(r.Resolve<ISingA>())).AsSingleton();
        using var c = b.Build();

        var ex = Assert.Throws<CircularDependencyException>(() => c.Resolve<ISingA>());
        Assert.Contains(typeof(ISingA), ex.Chain);
    }

    // --- A3: cross-thread singleton cycle must not deadlock ------------------------------------

    // Each constructor signals that it has started, then waits until BOTH constructors are running
    // before pulling in its dependency. This forces thread 1 to own A while thread 2 owns B, so each
    // then needs the singleton the other thread is still constructing.
    public static class Rendezvous
    {
        public static Barrier? Both;
    }

    public interface ICrossA { }
    public interface ICrossB { }

    public class CrossA : ICrossA
    {
        public CrossA(IResolver r)
        {
            Rendezvous.Both!.SignalAndWait(Timeout);
            _ = r.Resolve<ICrossB>();
        }
    }

    public class CrossB : ICrossB
    {
        public CrossB(IResolver r)
        {
            Rendezvous.Both!.SignalAndWait(Timeout);
            _ = r.Resolve<ICrossA>();
        }
    }

    [Fact]
    public void SingletonCycle_AcrossThreads_IsDetectedInsteadOfDeadlocking()
    {
        Rendezvous.Both = new Barrier(2);

        var b = new ContainerBuilder();
        b.Register<ICrossA, CrossA>().AsSingleton();
        b.Register<ICrossB, CrossB>().AsSingleton();
        using var c = b.Build();

        var t1 = Task.Run(() => Record.Exception(() => c.Resolve<ICrossA>()));
        var t2 = Task.Run(() => Record.Exception(() => c.Resolve<ICrossB>()));

        // A regression deadlocks here; the timeout turns that into a failure instead of a hang.
        Assert.True(Task.WaitAll(new Task[] { t1, t2 }, Timeout), "Cross-thread singleton cycle deadlocked.");

        var outcomes = new[] { t1.Result, t2.Result };
        // Neither thread can produce an instance: A needs B and B needs A.
        Assert.All(outcomes, e => Assert.NotNull(e));
        Assert.Contains(outcomes, e => e is CircularDependencyException);
        Assert.All(outcomes, e => Assert.IsType<CircularDependencyException>(e));
    }

    // Two unrelated singletons constructed concurrently must not serialize on each other: a slow
    // constructor on one thread must not block another thread building a different singleton.
    public static class SlowGate
    {
        public static ManualResetEventSlim? Release;
        public static ManualResetEventSlim? Started;
    }

    public sealed class SlowSingleton
    {
        public SlowSingleton()
        {
            SlowGate.Started!.Set();
            SlowGate.Release!.Wait(Timeout);
        }
    }

    public sealed class QuickSingleton { }

    [Fact]
    public void UnrelatedSingletons_DoNotSerializeOnEachOthersConstruction()
    {
        SlowGate.Release = new ManualResetEventSlim(false);
        SlowGate.Started = new ManualResetEventSlim(false);

        var b = new ContainerBuilder();
        b.Register<SlowSingleton>().AsSingleton();
        b.Register<QuickSingleton>().AsSingleton();
        using var c = b.Build();

        var slow = Task.Run(() => c.Resolve<SlowSingleton>());
        Assert.True(SlowGate.Started.Wait(Timeout), "Slow singleton constructor never started.");

        // While SlowSingleton is still inside its constructor, QuickSingleton must resolve promptly.
        var quick = Task.Run(() => c.Resolve<QuickSingleton>());
        Assert.True(quick.Wait(Timeout), "Unrelated singleton was blocked by another singleton's constructor.");

        SlowGate.Release.Set();
        Assert.True(slow.Wait(Timeout));
    }
}
