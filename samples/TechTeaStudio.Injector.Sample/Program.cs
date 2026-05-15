using TechTeaStudio.Injector;
using TechTeaStudio.Injector.Sample;

var builder = new ContainerBuilder();

// Singleton logger shared everywhere.
builder.Register<ILogger, ConsoleLogger>().AsSingleton();

// Transient greeter — fresh instance each resolve.
builder.Register<IGreeter, Greeter>().AsTransient();

// Decorator that wraps the greeter to time each call.
builder.RegisterDecorator<IGreeter, TimingGreeterDecorator>();

// Two keyed handlers so we can showcase ResolveDictionary.
builder.Register<IHandler, HelloHandler>().WithKey("hello").AsTransient();
builder.Register<IHandler, GoodbyeHandler>().WithKey("bye").AsTransient();

using IContainer container = builder.Build();

var greeter = container.Resolve<IGreeter>();
greeter.Greet("World");

var handlers = container.ResolveDictionary<string, IHandler>();
foreach (var kvp in handlers)
{
    Console.WriteLine($"[{kvp.Key}] {kvp.Value.Handle()}");
}

using IScope scope = container.CreateScope();
var unitOfWork = scope.Resolve<IGreeter>();
unitOfWork.Greet("scoped friend");

Console.WriteLine("done.");

namespace TechTeaStudio.Injector.Sample
{
    /// <summary>Minimal logging contract for the sample.</summary>
    public interface ILogger { void Info(string msg); }

    /// <summary>Console-backed logger.</summary>
    public sealed class ConsoleLogger : ILogger
    {
        public void Info(string msg) => Console.WriteLine($"[INFO] {msg}");
    }

    /// <summary>Sample service contract.</summary>
    public interface IGreeter { void Greet(string name); }

    /// <summary>Sample service — relies on a logger from the container.</summary>
    public sealed class Greeter : IGreeter
    {
        private readonly ILogger _logger;
        public Greeter(ILogger logger) { _logger = logger; }
        public void Greet(string name) => _logger.Info($"Hello, {name}!");
    }

    /// <summary>Decorator that times every call to the inner greeter.</summary>
    public sealed class TimingGreeterDecorator : IGreeter
    {
        private readonly IGreeter _inner;
        private readonly ILogger _logger;
        public TimingGreeterDecorator(IGreeter inner, ILogger logger) { _inner = inner; _logger = logger; }
        public void Greet(string name)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _inner.Greet(name);
            sw.Stop();
            _logger.Info($"Greet('{name}') took {sw.Elapsed.TotalMicroseconds:F1} µs");
        }
    }

    /// <summary>Keyed-handler contract for the ResolveDictionary demo.</summary>
    public interface IHandler { string Handle(); }
    /// <summary>"hello" handler.</summary>
    public sealed class HelloHandler : IHandler { public string Handle() => "Hi there"; }
    /// <summary>"bye" handler.</summary>
    public sealed class GoodbyeHandler : IHandler { public string Handle() => "See you"; }
}
