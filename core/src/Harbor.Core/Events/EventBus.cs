namespace Harbor.Core.Events;

/// <summary>Marker for simulation events. Events flow core → engine and between core modules
/// of the same layer (they must not reference each other directly).</summary>
public interface ISimEvent { }

/// <summary>Synchronous, single-threaded publish/subscribe bus owned by <see cref="Sim.SimWorld"/>.</summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : ISimEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
            _handlers[typeof(T)] = list = new List<Delegate>();
        list.Add(handler);
        return new Subscription(() => list.Remove(handler));
    }

    public void Publish<T>(T evt) where T : ISimEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        foreach (var h in list.ToArray()) ((Action<T>)h)(evt);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
