using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

#nullable enable

namespace CoreFtp.Infrastructure.Stream;

public sealed class FtpDataStream : System.IO.Stream
{
    private readonly FtpClient _client;
    private readonly System.IO.Stream _encapsulatedStream;
    private readonly ILogger? _logger;

    public override bool CanRead => _encapsulatedStream.CanRead;
    public override bool CanSeek => _encapsulatedStream.CanSeek;
    public override bool CanWrite => _encapsulatedStream.CanWrite;
    public override long Length => _encapsulatedStream.Length;

    public override long Position
    {
        get => _encapsulatedStream.Position;
        set => _encapsulatedStream.Position = value;
    }

    public FtpDataStream(System.IO.Stream encapsulatedStream, FtpClient client, ILogger? logger)
    {
        _logger = logger;
        _logger?.LogDebug("[FtpDataStream] Constructing");
        _encapsulatedStream = encapsulatedStream;
        _client = client;
    }

    protected override void Dispose(bool disposing)
    {
        _logger?.LogDebug("[FtpDataStream] Disposing");
        base.Dispose(disposing);

        try
        {
            _encapsulatedStream.Dispose();

            if (_client.Configuration.DisconnectTimeoutMilliseconds.HasValue)
            {
                _client.ControlStream.SetTimeouts(_client.Configuration.DisconnectTimeoutMilliseconds.Value);
            }
            _client.CloseFileDataStreamAsync().Wait();
        }
        catch (Exception disposeErr)
        {
            _logger?.LogWarning(disposeErr, "Closing the data stream took longer than expected");
        }
        finally
        {
            _client.ControlStream.ResetTimeouts();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("[FtpDataStream] FlushAsync");
        await _encapsulatedStream.FlushAsync(cancellationToken);
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _logger?.LogDebug("[FtpDataStream] ReadAsync");
        return await _encapsulatedStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _logger?.LogDebug("[FtpDataStream] WriteAsync");
        await _encapsulatedStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override void Flush()
    {
        _logger?.LogDebug("[FtpDataStream] Flush");
        _encapsulatedStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _logger?.LogDebug("[FtpDataStream] Read");
        return _encapsulatedStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _logger?.LogDebug("[FtpDataStream] Seek");
        return _encapsulatedStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _logger?.LogDebug("[FtpDataStream] SetLength");
        _encapsulatedStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _logger?.LogDebug("[FtpDataStream] Write");
        _encapsulatedStream.Write(buffer, offset, count);
    }
}
