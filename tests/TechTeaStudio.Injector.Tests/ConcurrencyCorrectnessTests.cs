using System.Collections.Concurrent;

using TechTeaStudio.Injector;

namespace TechTeaStudio.Injector.Tests;

/// <summary>
/// Concurrency-correctness tests covering two guarantees:
///   1. A singleton is constructed exactly once even under heavy parallel resolution.
///   2. Lazily closing an open-generic registration under concurrent load never corrupts the
///      by-service-type lookup (no InvalidOperationException / dictionary tearing).
/// Stress loops are intentionally bounded so the suite stays fast.
/// </summary>
public class ConcurrencyCorrectnessTests
{
    public sealed class CountingSingleton
    {
        public static int Constructions;
        public CountingSingleton() => Interlocked.Increment(ref Constructions);
    }

    [Fact]
    public void Singleton_ConstructedExactlyOnce_UnderParallelResolution()
    {
        CountingSingleton.Constructions = 0;

        var b = new ContainerBuilder();
        b.Register<CountingSingleton>().AsSingleton();
        using var c = b.Build();

        const int workers = 100;
        var results = new CountingSingleton[workers];

        Parallel.For(0, workers, i => results[i] = c.Resolve<CountingSingleton>());

        Assert.Equal(1, CountingSingleton.Constructions);

        // Every resolver must observe the very same instance.
        var first = results[0];
        Assert.NotNull(first);
        Assert.All(results, r => Assert.Same(first, r));
    }

    public interface IService
    {
        int Value { get; }
    }

    public sealed class CountingService : IService
    {
        public static int Constructions;
        public CountingService() => Interlocked.Increment(ref Constructions);
        public int Value => 42;
    }

    [Fact]
    public async Task Singleton_ByInterface_ConstructedOnce_WithTaskWhenAll()
    {
        CountingService.Constructions = 0;

        var b = new ContainerBuilder();
        b.Register<IService, CountingService>().AsSingleton();
        using var c = b.Build();

        const int workers = 100;
        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(() => c.Resolve<IService>()))
            .ToArray();

        var resolved = await Task.WhenAll(tasks);

        Assert.Equal(1, CountingService.Constructions);
        var first = resolved[0];
        Assert.All(resolved, r => Assert.Same(first, r));
    }

    // ---- Open-generic synthesis race ------------------------------------------------------

    public interface IRepository<T>
    {
        Type Element { get; }
    }

    public sealed class Repository<T> : IRepository<T>
    {
        public Type Element => typeof(T);
    }

    // A set of distinct closed types so many DISTINCT IRepository<TN> get synthesized at once.
    public sealed class T01 { } public sealed class T02 { } public sealed class T03 { }
    public sealed class T04 { } public sealed class T05 { } public sealed class T06 { }
    public sealed class T07 { } public sealed class T08 { } public sealed class T09 { }
    public sealed class T10 { } public sealed class T11 { } public sealed class T12 { }
    public sealed class T13 { } public sealed class T14 { } public sealed class T15 { }
    public sealed class T16 { }

    public sealed class OtherDependency { }

    private static readonly Type[] ClosedElementTypes =
    {
        typeof(T01), typeof(T02), typeof(T03), typeof(T04),
        typeof(T05), typeof(T06), typeof(T07), typeof(T08),
        typeof(T09), typeof(T10), typeof(T11), typeof(T12),
        typeof(T13), typeof(T14), typeof(T15), typeof(T16),
    };

    [Fact]
    public void OpenGeneric_ConcurrentDistinctClosings_DoNotCorruptLookup()
    {
        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(Repository<>)).AsSingleton();
        b.Register<OtherDependency>().AsTransient();
        using var c = b.Build();

        // Hammer the same set of distinct closed generics from many threads at once, while a
        // separate set of tasks resolves an unrelated service in parallel. This exercises the
        // first-time synthesis of `IRepository<Tn>` (which writes _byServiceType) racing against
        // lock-free readers of _byServiceType.
        const int iterations = 200;
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.For(0, iterations, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 2 }, i =>
        {
            try
            {
                var elem = ClosedElementTypes[i % ClosedElementTypes.Length];
                var repoType = typeof(IRepository<>).MakeGenericType(elem);
                var repo = c.Resolve(repoType);
                Assert.NotNull(repo);

                // Interleave an unrelated resolve so a reader hits _byServiceType while another
                // thread may be synthesizing a closed generic.
                _ = c.Resolve<OtherDependency>();
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Assert.True(exceptions.IsEmpty,
            "Concurrent open-generic closing threw: " + string.Join(" | ", exceptions.Select(e => e.ToString())));
    }

    // Shared (NOT per-closed-type) construction counter so every CountingGenericRepo<T> reports
    // into the same dictionary, keyed by element type.
    public static class GenericRepoCounter
    {
        public static readonly ConcurrentDictionary<Type, int> ConstructionsByElement = new();
    }

    public sealed class CountingGenericRepo<T> : IRepository<T>
    {
        public CountingGenericRepo()
            => GenericRepoCounter.ConstructionsByElement.AddOrUpdate(typeof(T), 1, (_, n) => n + 1);
        public Type Element => typeof(T);
    }

    [Fact]
    public void OpenGenericSingleton_EachClosedType_ConstructedOnce_UnderParallelLoad()
    {
        GenericRepoCounter.ConstructionsByElement.Clear();

        var b = new ContainerBuilder();
        b.Register(typeof(IRepository<>), typeof(CountingGenericRepo<>)).AsSingleton();
        using var c = b.Build();

        const int workersPerType = 32;

        // For each distinct closed type, resolve it from many threads concurrently. Running the
        // outer loop in parallel too maximizes the chance of overlapping first-time synthesis.
        Parallel.ForEach(ClosedElementTypes, elem =>
        {
            var repoType = typeof(IRepository<>).MakeGenericType(elem);
            Parallel.For(0, workersPerType, _ => c.Resolve(repoType));
        });

        // Each closed-generic singleton must have been constructed exactly once.
        foreach (var elem in ClosedElementTypes)
        {
            Assert.True(GenericRepoCounter.ConstructionsByElement.TryGetValue(elem, out var count),
                $"No construction recorded for IRepository<{elem.Name}>.");
            Assert.Equal(1, count);
        }
    }
}
