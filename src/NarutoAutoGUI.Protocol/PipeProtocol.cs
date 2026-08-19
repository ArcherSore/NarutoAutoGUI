using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace NarutoAutoGUI.Protocol;

public sealed class ProtocolConnection : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public ProtocolConnection(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public async Task<WireEnvelope?> ReadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var prefix = new byte[sizeof(uint)];
        var firstRead = await _stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken);
        if (firstRead == 0)
        {
            return null;
        }

        await ReadExactlyAsync(_stream, prefix.AsMemory(1), cancellationToken);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (payloadLength == 0 || payloadLength > ProtocolConstants.MaximumFramePayloadBytes)
        {
            throw new ProtocolException($"非法 IPC frame 长度：{payloadLength}。 ");
        }

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)payloadLength));
        await ReadExactlyAsync(_stream, payload, cancellationToken);

        string json;
        try
        {
            json = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ProtocolException("IPC frame 不是合法 UTF-8。", exception);
        }

        try
        {
            return JsonSerializer.Deserialize<WireEnvelope>(json, ProtocolJson.Options)
                   ?? throw new ProtocolException("IPC envelope 为空。 ");
        }
        catch (JsonException exception)
        {
            throw new ProtocolException("IPC envelope 不是合法 JSON。", exception);
        }
    }

    public async Task WriteAsync(WireEnvelope envelope, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson.Options);
        if (payload.Length == 0 || payload.Length > ProtocolConstants.MaximumFramePayloadBytes)
        {
            throw new ProtocolException($"IPC frame payload 越界：{payload.Length} bytes。 ");
        }

        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)payload.Length));

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(prefix, cancellationToken);
            await _stream.WriteAsync(payload, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
        await _stream.DisposeAsync();
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("IPC frame 在 payload 完成前结束。 ");
            }
            read += count;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
