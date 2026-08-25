using System.Collections.Concurrent;

namespace AscensionNetTool;

/// <summary>In-process pub/sub for multi-instance host coordination.</summary>
static class EventBus
{
    static readonly ConcurrentDictionary<Type, ConcurrentBag<object>> Handlers = new();

    public static IDisposable Subscribe<T>(Action<T> handler)
    {
        var bag = Handlers.GetOrAdd(typeof(T), _ => new ConcurrentBag<object>());
        bag.Add(handler!);
        return new Sub(() => bag.TryTake(out _));
    }

    public static void Publish<T>(T evt)
    {
        if (!Handlers.TryGetValue(typeof(T), out var bag))
            return;
        foreach (var h in bag)
        {
            try { ((Action<T>)h)(evt); }
            catch { }
        }
    }

    sealed class Sub(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

readonly record struct InstanceLaunchedEvent(int Id, uint Pid);
readonly record struct InstanceDiedEvent(int Id, uint Pid);
readonly record struct ObjectDiscoveredEvent(int SrcInstance, ulong Guid, uint Entry);
readonly record struct PacketObservedEvent(int SrcInstance, CapturedPacket Packet);
readonly record struct SharedUpdatedEvent(int ObjectCount, int InstanceCount);
