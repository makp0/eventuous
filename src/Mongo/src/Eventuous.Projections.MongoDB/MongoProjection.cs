using Eventuous.Projections.MongoDB.Tools;
using Microsoft.Extensions.Logging;

namespace Eventuous.Projections.MongoDB; 

[PublicAPI]
public abstract class MongoProjection<T> : IEventHandler
    where T : ProjectedDocument {
    readonly ILogger? _log;

    protected IMongoCollection<T> Collection { get; }

    protected MongoProjection(IMongoDatabase database, ILoggerFactory? loggerFactory) {
        var log = loggerFactory?.CreateLogger(GetType());
        _log           = log?.IsEnabled(LogLevel.Debug) == true ? log : null;
        Collection     = Ensure.NotNull(database, nameof(database)).GetDocumentCollection<T>();
    }

    public async Task HandleEvent(ReceivedEvent evt, CancellationToken cancellationToken) {
        var updateTask = GetUpdate(evt);
        var update     = updateTask == NoOp ? null : await updateTask.NoContext();

        if (update == null) {
            _log?.LogDebug("No handler for {Event}", evt.Payload!.GetType().Name);
            return;
        }

        _log?.LogDebug("Projecting {Event}", evt.Payload!.GetType().Name);

        var task = update switch {
            OtherOperation<T> operation => operation.Execute(),
            CollectionOperation<T> col  => col.Execute(Collection, cancellationToken),
            UpdateOperation<T> upd      => ExecuteUpdate(upd),
            _                           => Task.CompletedTask
        };

        await task.NoContext();

        Task ExecuteUpdate(UpdateOperation<T> upd)
            => Collection.UpdateOneAsync(
                upd.Filter,
                upd.Update.Set(x => x.Position, (long)evt.StreamPosition),
                new UpdateOptions { IsUpsert = true },
                cancellationToken
            );
    }
    
    protected abstract ValueTask<Operation<T>> GetUpdate(object evt, long? position);

    protected virtual ValueTask<Operation<T>> GetUpdate(ReceivedEvent receivedEvent) {
        return GetUpdate(receivedEvent.Payload!, (long?)receivedEvent.StreamPosition);
    }

    protected Operation<T> UpdateOperation(BuildFilter<T> filter, BuildUpdate<T> update)
        => new UpdateOperation<T>(filter(Builders<T>.Filter), update(Builders<T>.Update));

    protected ValueTask<Operation<T>> UpdateOperationTask(BuildFilter<T> filter, BuildUpdate<T> update)
        => new(UpdateOperation(filter, update));

    protected Operation<T> UpdateOperation(string id, BuildUpdate<T> update)
        => UpdateOperation(filter => filter.Eq(x => x.Id, id), update);

    protected ValueTask<Operation<T>> UpdateOperationTask(string id, BuildUpdate<T> update)
        => new(UpdateOperation(id, update));

    protected static readonly ValueTask<Operation<T>> NoOp = new((Operation<T>) null!);
}

public abstract record Operation<T>;

public record UpdateOperation<T>(FilterDefinition<T> Filter, UpdateDefinition<T> Update) : Operation<T>;

public record OtherOperation<T>(Func<Task> Execute) : Operation<T>;

public record CollectionOperation<T>(Func<IMongoCollection<T>, CancellationToken, Task> Execute) : Operation<T>;