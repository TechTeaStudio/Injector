namespace TechTeaStudio.Injector.Benchmarks;

// Shared object graph used identically by every container under test so the
// comparison stays fair. A small 3-level constructor dependency chain plus an
// open-generic repository pair.

public interface ILevel0
{
    int Tick();
}

public interface ILevel1
{
    ILevel0 Inner { get; }
}

public interface ILevel2
{
    ILevel1 Inner { get; }
}

public sealed class Level0 : ILevel0
{
    private int _n;
    public int Tick() => ++_n;
}

public sealed class Level1 : ILevel1
{
    public Level1(ILevel0 inner) => Inner = inner;
    public ILevel0 Inner { get; }
}

public sealed class Level2 : ILevel2
{
    public Level2(ILevel1 inner) => Inner = inner;
    public ILevel1 Inner { get; }
}

// Singleton-flavoured service (stateless, cheap to share).
public interface ISingletonService
{
    long Id { get; }
}

public sealed class SingletonService : ISingletonService
{
    public long Id { get; } = 42;
}

// Scoped-flavoured service with a dependency so scoped resolution actually
// walks a graph rather than returning a leaf.
public interface IScopedService
{
    ILevel0 Dependency { get; }
}

public sealed class ScopedService : IScopedService
{
    public ScopedService(ILevel0 dependency) => Dependency = dependency;
    public ILevel0 Dependency { get; }
}

// Open generic pair: IRepository<T> -> Repository<T>.
public interface IRepository<T>
{
    Type ItemType { get; }
}

public sealed class Repository<T> : IRepository<T>
{
    public Type ItemType => typeof(T);
}

// Distinct closed types so the open-generic path closes more than one shape.
public sealed class Customer;

public sealed class Order;
