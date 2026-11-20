using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Alachisoft.NCache.Client;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace NCache.OSS.Quartz.Tests;

public class FaultInjectingCache : DispatchProxy
{
    public ICache Inner { get; private set; } = null!;

    public Func<MethodInfo, object?[]?, bool>? FailWhen { get; set; }

    public Func<MethodInfo, object?[]?, TimeSpan?>? DelayAfter { get; set; }

    public Func<MethodInfo, object?[]?, bool>? PartialInsertBulk { get; set; }

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void ResetCalls() => Interlocked.Exchange(ref _calls, 0);

    public static (ICache Cache, FaultInjectingCache Control) Wrap(ICache inner)
    {
        var proxy = Create<ICache, FaultInjectingCache>();
        var control = (FaultInjectingCache) (object) proxy;
        control.Inner = inner;
        return (proxy, control);
    }

    public static Func<MethodInfo, object?[]?, bool> Times(int times, Func<MethodInfo, object?[]?, bool> match)
    {
        var remaining = times;
        return (method, args) => match(method, args) && Interlocked.Decrement(ref remaining) >= 0;
    }

    public static Func<MethodInfo, object?[]?, bool> Call(string methodName, Func<string, bool>? key = null)
        => (method, args) => method.Name == methodName && (key is null || (args is { Length: > 0 } && args[0] is string k && key(k)));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Interlocked.Increment(ref _calls);

        if (FailWhen?.Invoke(targetMethod!, args) == true)
        {
            throw new InvalidOperationException($"Simulated cache failure in {targetMethod!.Name}({args?.FirstOrDefault()})");
        }

        if (PartialInsertBulk?.Invoke(targetMethod!, args) == true && args?[0] is IDictionary<string, CacheItem> items)
        {
            var kept = items.Where(kv => !kv.Key.Contains(":fired:", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);
            args[0] = kept;
            targetMethod!.Invoke(Inner, args);
            throw new InvalidOperationException($"Simulated partial bulk-write failure ({items.Count - kept.Count} key(s) not written)");
        }

        object? result;

        try
        {
            result = targetMethod!.Invoke(Inner, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }

        if (DelayAfter?.Invoke(targetMethod!, args) is { } delay)
        {
            Thread.Sleep(delay);
        }

        return result;
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public ILogger<T> CreateLogger<T>() => new CapturingLogger<T>(this);

    public bool Contains(string text) => Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    public void Dispose()
    {
    }

    private sealed class CapturingLogger<T>(CapturingLoggerProvider owner) : CapturingLogger(owner), ILogger<T>
    {
    }

    private class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

