namespace Eventuous.Subscriptions;

public class EventSubscription1 : ICanStop {
    readonly ICanStop _inner;

    public EventSubscription1(ICanStop inner) {
        _inner = inner;
    }

    public Task Stop(CancellationToken cancellationToken = default) => _inner.Stop(cancellationToken);
}

public class Stoppable : ICanStop {
    readonly Action _stop;

    public Stoppable(Action stop) => _stop = stop;

    public Task Stop(CancellationToken cancellationToken) {
        _stop();
        return Task.CompletedTask;
    }
}

public interface ICanStop {
    Task Stop(CancellationToken cancellationToken = default);
}