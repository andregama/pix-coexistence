using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ConvivenciaPix.V2.BacenSpiSimulator;

public sealed record SimulatedMessage(string PiResourceId, byte[] Body);

/// <summary>What the simulator received on its "in" endpoint.</summary>
public sealed record ReceivedMessage(string PiResourceId, string MsgType, string? MsgId, byte[] Body);

/// <summary>
/// In-memory stand-in for the SPI's message queues: what the participant sent, what is waiting to
/// be pulled, and which batches were delivered but not yet acknowledged.
/// </summary>
public sealed class SimulatorState
{
    private readonly object _gate = new();
    private readonly LinkedList<SimulatedMessage> _pending = new();
    private readonly Dictionary<string, List<SimulatedMessage>> _unacked = new();
    private TaskCompletionSource _arrival = NewSignal();

    public ConcurrentQueue<ReceivedMessage> Received { get; } = new();

    public static string NewResourceId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public static string NewStreamId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public int UnackedCount { get { lock (_gate) return _unacked.Values.Sum(b => b.Count); } }

    /// <summary>Queues a message for the participant to pull. Returns its PI-ResourceId.</summary>
    public string Enqueue(byte[] body, string? piResourceId = null)
    {
        var message = new SimulatedMessage(piResourceId ?? NewResourceId(), body);
        TaskCompletionSource signal;
        lock (_gate)
        {
            _pending.AddLast(message);
            signal = _arrival;
            _arrival = NewSignal();
        }
        signal.TrySetResult();
        return message.PiResourceId;
    }

    /// <summary>Confirms the batch delivered under the stream id. Unknown ids are ignored.</summary>
    public void Ack(string streamId)
    {
        lock (_gate) _unacked.Remove(streamId);
    }

    /// <summary>
    /// A new stream means the previous one was abandoned: everything delivered but never confirmed
    /// goes back to the front of the queue, as the SPI redelivers unconfirmed messages.
    /// </summary>
    public void RequeueUnacked()
    {
        lock (_gate)
        {
            foreach (var message in _unacked.Values.SelectMany(b => b).Reverse())
                _pending.AddFirst(message);
            _unacked.Clear();
        }
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for messages and hands out at most <paramref name="max"/>,
    /// remembering them under <paramref name="nextStreamId"/> until that id is acknowledged.
    /// </summary>
    public async Task<IReadOnlyList<SimulatedMessage>> TakeAsync(
        string nextStreamId, int max, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_pending.Count > 0)
                {
                    var batch = new List<SimulatedMessage>();
                    while (batch.Count < max && _pending.First is { } node)
                    {
                        batch.Add(node.Value);
                        _pending.RemoveFirst();
                    }
                    _unacked[nextStreamId] = batch;
                    return batch;
                }
                signal = _arrival.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return [];

            await Task.WhenAny(signal, Task.Delay(remaining, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
