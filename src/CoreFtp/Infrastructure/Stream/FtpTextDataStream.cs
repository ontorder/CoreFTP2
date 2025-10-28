using CoreFtp.Enum;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CoreFtp.Infrastructure.Stream;

public sealed class FtpTextDataStream
{
    private TaskCompletionSource? _bugMaybeReadToEndTask = null;
    private bool _closing = false;
    private readonly FtpClientConfiguration _configuration;
    private readonly NetworkStream _ftpStream;
    private readonly ILogger? _logger;
    private SslStream? _sslStream;

    public FtpTextDataStream(
        FtpClientConfiguration configuration,
        ILogger? logger,
        NetworkStream ftpStream)
    {
        _configuration = configuration;
        _ftpStream = ftpStream;
        _logger = logger;
    }

    public async Task CloseAsync()
    {
        _closing = true;
        if (_bugMaybeReadToEndTask != null) await _bugMaybeReadToEndTask.Task;

        _ftpStream.Close();
        _sslStream?.Close();
        _ftpStream.Dispose();
    }

    public System.IO.Stream GetStream()
        => _sslStream ?? (System.IO.Stream)_ftpStream;

    public async IAsyncEnumerable<string> ReadLineAsyncEnum([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int MaxReadSize = 512;
        const int TempTimeout = 100; // ms

        var data = new ArrayBufferWriter<byte>();
        PartialSplitStatus splitStatus = new();
        int count = 0;
        var stream = GetStream();
        _bugMaybeReadToEndTask = new();
        var readTimeout = TimeSpan.FromMilliseconds(TempTimeout);

        do
        {
            var buf = new byte[MaxReadSize];
            var cts = new CancellationTokenSource(readTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            try
            {
                count = await stream.ReadAsync(buf, linkedCts.Token);
                if (count == 0)
                {
                    _bugMaybeReadToEndTask.SetResult();
                    break;
                }
            }
            catch (TimeoutException)
            {
                if (_closing)
                {
                    _bugMaybeReadToEndTask.SetResult();
                    break;
                }
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception readErr)
            {
                _logger?.LogError(readErr, "[CoreFtp] EXCEPTION reading from FTP data stream");
                break;
            }

            data.Write(buf.AsSpan()[..count]);
            foreach (string line in SplitEncodePartial(data.WrittenSpan, splitStatus))
                yield return line;
        }
        while (count > 0 || _closing);
    }

    public async Task TryActivateEncryptionAsync()
    {
        if (_configuration.ShouldEncrypt && _configuration.EncryptionType == FtpEncryption.Explicit)
            await ActivateEncryptionAsync();

        _logger?.LogDebug("[CoreFtp] Waiting for welcome message");
    }

    private async Task ActivateEncryptionAsync()
    {
        try
        {
            _sslStream = new SslStream(_ftpStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
            await _sslStream.AuthenticateAsClientAsync(_configuration.Host, _configuration.ClientCertificates, _configuration.SslProtocols, true);
        }
        catch (AuthenticationException authErr)
        {
            _logger?.LogError(authErr, "[CoreFtp] Could not activate encryption for the connection");
            throw;
        }
    }

    private static ICollection<string> SplitEncodePartial(ReadOnlySpan<byte> writtenSpan, PartialSplitStatus status)
    {
        List<string> lines = new(1);

        do
        {
            int newline_pos = writtenSpan[status.PreviousPosition..].IndexOf((byte)'\n');
            if (newline_pos == -1) break;
            newline_pos += status.PreviousPosition;
            var token = writtenSpan[status.PreviousPosition..newline_pos].TrimEnd((byte)'\r');
            string encoded = Encoding.ASCII.GetString(token);
            lines.Add(encoded);
            status.PreviousPosition = newline_pos + 1;
        }
        while (status.PreviousPosition < writtenSpan.Length);

        return lines;
    }

    private bool OnValidateCertificate(X509Certificate _, X509Chain __, SslPolicyErrors errors)
        => _configuration.IgnoreCertificateErrors || errors == SslPolicyErrors.None;

    private sealed class PartialSplitStatus
    {
        public int PreviousPosition = 0;
    }
}
