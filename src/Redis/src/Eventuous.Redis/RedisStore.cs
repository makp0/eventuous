﻿using System.Globalization;
using System.Runtime.Serialization;
using System.Text;
using Eventuous.Diagnostics;
using StackExchange.Redis;

namespace Eventuous.Redis;

public delegate IDatabase GetRedisDatabase();

public class RedisStore : IEventStore {
    readonly GetRedisDatabase    _getDatabase;
    readonly IEventSerializer    _serializer;
    readonly IMetadataSerializer _metaSerializer;

    public RedisStore(
        GetRedisDatabase     getDatabase,
        IEventSerializer?    serializer     = null,
        IMetadataSerializer? metaSerializer = null
    ) {
        _serializer     = serializer     ?? DefaultEventSerializer.Instance;
        _metaSerializer = metaSerializer ?? DefaultMetadataSerializer.Instance;
        _getDatabase    = Ensure.NotNull(getDatabase, "Connection factory");
    }

    const string ContentType = "application/json";

    public async Task<StreamEvent[]> ReadEvents(
        StreamName         stream,
        StreamReadPosition start,
        int                count,
        CancellationToken  cancellationToken
    ) {
        var result = await _getDatabase().StreamRangeAsync(stream.ToString(), ULongToStreamId(start), null, count);
        if (result == null) throw new StreamNotFound(stream);

        return result.Select(x => ToStreamEvent(x)).ToArray();
    }

    public async Task<AppendEventsResult> AppendEvents(
        StreamName                       stream,
        ExpectedStreamVersion            expectedVersion,
        IReadOnlyCollection<StreamEvent> events,
        CancellationToken                cancellationToken
    ) {
        var keys = new object[] {
            "append_events",
            3,
            stream.ToString(),
            expectedVersion.Value,
            DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)
        };

        var args = events
            .Where(x => x.Payload != null)
            .Aggregate(
                new List<object>(),
                (agg, x) => {
                    agg.AddRange(ConvertStreamEvent(x));
                    return agg;
                }
            )
            .ToArray();

        var fCallParams = new object[keys.Length + args.Length];
        keys.CopyTo(fCallParams, 0);
        args.CopyTo(fCallParams, keys.Length);

        var database = _getDatabase();

        try {
            var response = (string[]?)await database.ExecuteAsync("FCALL", fCallParams);
            return new AppendEventsResult(StreamIdToULong(response?[1]), Convert.ToInt64(response?[0]));
        }
        catch (Exception e) when (e.Message.Contains("WrongExpectedVersion")) {
            PersistenceEventSource.Log.UnableToAppendEvents(stream, e);
            throw new AppendToStreamException(stream, e);
        }

        object[] ConvertStreamEvent(StreamEvent evt) {
            var data = _serializer.SerializeEvent(evt.Payload!);
            var meta = _metaSerializer.Serialize(evt.Metadata);
            return new object[] { evt.Id.ToString(), data.EventType, AsString(data.Payload), AsString(meta) };
        }

        string AsString(ReadOnlySpan<byte> bytes)
            => Encoding.UTF8.GetString(bytes);
    }

    public async Task<bool> StreamExists(StreamName stream, CancellationToken cancellationToken) {
        var database = _getDatabase();
        var info     = await database.StreamInfoAsync(stream.ToString());

        return (info.Length > 0)
            ? true
            : false;
    }

    public Task TruncateStream(
        StreamName             stream,
        StreamTruncatePosition truncatePosition,
        ExpectedStreamVersion  expectedVersion,
        CancellationToken      cancellationToken
    )
        => throw new NotImplementedException();

    public Task DeleteStream(
        StreamName            stream,
        ExpectedStreamVersion expectedVersion,
        CancellationToken     cancellationToken
    )
        => throw new NotImplementedException();

    StreamEvent ToStreamEvent(StreamEntry evt) {
        var deserialized = _serializer.DeserializeEvent(
            Encoding.UTF8.GetBytes(evt["json_data"].ToString()),
            evt["message_type"].ToString(),
            ContentType
        );

        var meta = (string?)evt["json_metadata"] == null
            ? new Metadata()
            : _metaSerializer.Deserialize(Encoding.UTF8.GetBytes(evt["json_metadata"]!));

        return deserialized switch {
            SuccessfullyDeserialized success => AsStreamEvent(success.Payload),
            FailedToDeserialize failed => throw new SerializationException(
                $"Can't deserialize {evt["message_type"]}: {failed.Error}"
            ),
            _ => throw new Exception("Unknown deserialization result")
        };

        StreamEvent AsStreamEvent(object payload)
            => new(Guid.Parse(evt["message_id"].ToString()), payload, meta ?? new Metadata(), ContentType, StreamIdToLong(evt.Id));
    }

    /*
        16761513606580 -> 1676151360658-0
    */
    static RedisValue ULongToStreamId(StreamReadPosition position) {
        if (position == StreamReadPosition.Start) return "0-0";

        return new RedisValue($"{position.Value / 10}-{position.Value % 10}");
    }

    /*
        1676151360658-0 -> 16761513606580
    */
    static ulong StreamIdToULong(RedisValue value) {
        var parts = ((string?)value)?.Split('-');
        return Convert.ToUInt64(parts?[0]) * 10 + Convert.ToUInt64(parts?[1]);
    }

    /*
        1676151360658-0 -> 16761513606580
    */
    static long StreamIdToLong(RedisValue value) {
        var parts = ((string?)value)?.Split('-');
        return Convert.ToInt64(parts?[0]) * 10 + Convert.ToInt64(parts?[1]);
    }
}