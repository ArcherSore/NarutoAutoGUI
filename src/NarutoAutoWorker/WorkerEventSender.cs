using System.Threading.Channels;
using NarutoAutoGUI.Protocol;

namespace NarutoAutoWorker;

internal sealed class WorkerEventSender : IAsyncDisposable
{
    private readonly ProtocolConnection _connection;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<WireEnvelope> _stateEvents = Channel.CreateBounded<WireEnvelope>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Channel<WireEnvelope> _logEvents = Channel.CreateBounded<WireEnvelope>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Task _writerTask;

    internal WorkerEventSender(ProtocolConnection connection)
    {
        _connection = connection;
        _writerTask = Task.Run(WriteLoopAsync);
    }

    internal void PublishState(WireEnvelope envelope) => _stateEvents.Writer.TryWrite(envelope);

    internal void PublishLog(WireEnvelope envelope) => _logEvents.Writer.TryWrite(envelope);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _stateEvents.Writer.TryComplete();
        _logEvents.Writer.TryComplete();
        try
        {
            await _writerTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        _shutdown.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            if (_stateEvents.Reader.TryRead(out var state))
            {
                await _connection.WriteAsync(state, _shutdown.Token);
                continue;
            }
            if (_logEvents.Reader.TryRead(out var log))
            {
                await _connection.WriteAsync(log, _shutdown.Token);
                continue;
            }

            var stateReady = _stateEvents.Reader.WaitToReadAsync(_shutdown.Token).AsTask();
            var logReady = _logEvents.Reader.WaitToReadAsync(_shutdown.Token).AsTask();
            await Task.WhenAny(stateReady, logReady);
        }
    }
}
