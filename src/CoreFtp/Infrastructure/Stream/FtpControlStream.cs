using CoreFtp.Components.DnsResolution;
using CoreFtp.Enum;
using CoreFtp.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CoreFtp.Infrastructure.Stream;

public sealed partial class FtpControlStream
{
    public Encoding Encoding { get; set; } = Encoding.ASCII;
    public bool IsEncrypted => SslStream != null && SslStream.IsEncrypted;
    public ILogger? Logger;

    private readonly FtpClientConfiguration Configuration;
    private readonly IDnsResolver DnsResolver;
    private static readonly Regex FtpRegex = CreateFtpRegex();
    private Socket? FtpSocket;
    private NetworkStream? _ftpStream;
    private DateTime LastActivity = DateTime.Now;
    private readonly SemaphoreSlim ReceiveSemaphore = new(1, 1);
    private readonly SemaphoreSlim Semaphore = new(1, 1);
    private SslStream? SslStream { get; set; }

    private const int SocketPollInterval = 15000;
    private const int SecondsToMilli = 1000;

    public bool IsConnected
    {
        get
        {
            try
            {
                if (FtpSocket?.Connected != true || _ftpStream?.CanRead != true || _ftpStream?.CanWrite != true)
                {
                    Disconnect();
                    return false;
                }

                if (LastActivity.HasIntervalExpired(DateTime.Now, SocketPollInterval))
                {
                    Logger?.LogDebug("[CoreFtp] Polling connection");
                    if (FtpSocket?.Poll(500000, SelectMode.SelectRead) == true && FtpSocket.Available == 0)
                    {
                        Disconnect();
                        return false;
                    }
                }
            }
            catch (SocketException socketException)
            {
                Disconnect();
                Logger?.LogError(socketException, "[CoreFtp] FtpSocketStream.IsConnected: Caught and discarded SocketException while testing for connectivity");
                return false;
            }
            catch (IOException ioException)
            {
                Disconnect();
                Logger?.LogError(ioException, "[CoreFtp] FtpSocketStream.IsConnected: Caught and discarded IOException while testing for connectivity");
                return false;
            }

            return true;
        }
    }

    internal bool IsDataConnection { get; set; }

    public FtpControlStream(FtpClientConfiguration configuration, IDnsResolver dnsResolver)
    {
        Logger?.LogDebug("[CoreFtp] Constructing new FtpSocketStream");
        Configuration = configuration;
        DnsResolver = dnsResolver;
    }

    public async Task ConnectAsync(CancellationToken token = default)
    {
        await ConnectStreamAsync(Configuration.Host, Configuration.Port, token);

        if (Configuration.ShouldEncrypt == false)
            return;

        if (false == IsConnected || IsEncrypted)
            return;

        if (Configuration.EncryptionType == FtpEncryption.Implicit)
            await EncryptImplicitly(token);

        if (Configuration.EncryptionType == FtpEncryption.Explicit)
            await EncryptExplicitly(token);
    }

    public void Disconnect()
    {
        Logger?.LogTrace("[CoreFtp] Disconnecting");
        try
        {
            _ftpStream?.Dispose();
            SslStream?.Dispose();
            FtpSocket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception)
        {
            Logger?.LogError(exception, "[CoreFtp] Exception caught");
        }
        finally
        {
            FtpSocket = null;
            _ftpStream = null;
        }
    }

    public void Dispose(bool disposing)
    {
        Logger?.LogTrace("[CoreFtp] {msg}", IsDataConnection ? "Disposing of data connection" : "Disposing of control connection");
        if (disposing) Disconnect();
    }

    public void Flush()
    {
        if (false == IsConnected)
            throw new InvalidOperationException("The FtpSocketStream object is not connected.");

        _ftpStream?.Flush();
    }

    public async Task<FtpResponse> GetFtpResponseAsync(CancellationToken token = default)
    {
        //Logger?.LogTrace("[CoreFtp] Getting Response");

        if (Encoding == null)
            throw new ArgumentNullException(nameof(Encoding));

        await ReceiveSemaphore.WaitAsync(token);

        try
        {
            token.ThrowIfCancellationRequested();

            var response = new FtpResponse();
            var data = new List<string>();

            foreach (string line in await ReadLinesAsync(Encoding, token))
            {
                token.ThrowIfCancellationRequested();
                Logger?.LogDebug("[CoreFtp] {line}", line);
                data.Add(line);

                Match match = FtpRegex.Match(line);
                if (false == match.Success)
                    continue;
                //Logger?.LogTrace("[CoreFtp] Finished receiving message");
                response.FtpStatusCode = match.Groups["statusCode"].Value.ToStatusCode();
                response.ResponseMessage = match.Groups["message"].Value;
                break;
            }
            response.Data = data.ToArray();
            return response;
        }
        finally
        {
            ReceiveSemaphore.Release();
        }
    }

    public async Task<IAsyncEnumerable<string>> GetResponseReaderAsync(CancellationToken token = default)
    {
        if (Encoding == null)
            throw new ArgumentNullException(nameof(Encoding));

        await ReceiveSemaphore.WaitAsync(token);

        try
        {
            return ReadLineAsyncEnum(Encoding, token);
        }
        finally
        {
            ReceiveSemaphore.Release();
        }
    }

    public System.IO.Stream GetStream()
        => SslStream ?? (System.IO.Stream)_ftpStream;

    public async Task<FtpControlStream> OpenDataStreamAsync(string host, int port, CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] FtpSocketStream: Opening datastream");
        var socketStream = new FtpControlStream(Configuration, DnsResolver) { Logger = Logger, IsDataConnection = true };
        await socketStream.ConnectStreamAsync(host, port, token);

        if (IsEncrypted)
            await socketStream.ActivateEncryptionAsync();
        return socketStream;
    }

    public async IAsyncEnumerable<string> ReadLineAsync_DEBUG(Encoding encoding, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        int count;
        var data = new List<byte>(10);
        byte[] single = new byte[1];

    loop:
        {
            await FtpSocket!.ReceiveAsync(single, SocketFlags.Peek, cancellationToken);
            count = await _ftpStream!.ReadAsync(single, cancellationToken);
            if (count == 0) yield break;

            data.Add(single[0]);
            if (data.Count > 100_000) throw new ArgumentOutOfRangeException(); // non convinto

            if (single[0] == '\n')
            {
                string ascii = Encoding.ASCII.GetString(data.ToArray());
                yield return ascii.TrimEnd();
                data.Clear();
            }
            goto loop;
        }
    }

    public async IAsyncEnumerable<string> ReadLineAsync_DEBUG2(Encoding encoding, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        const int MaxReadSize = 512;
        const byte Lf = (byte)'\n';

        var data = new List<byte>(10);
        byte[] peekBuf = new byte[MaxReadSize];

    loop:
        {
            int peekCount = await FtpSocket!.ReceiveAsync(peekBuf, SocketFlags.Peek, cancellationToken);
            if (peekCount == 0) yield break;

            int pos = Array.IndexOf(peekBuf, Lf);
            if (pos < 0)
            {
                byte[] accum = new byte[peekCount];
                int accumCount = await FtpSocket.ReceiveAsync(accum, cancellationToken);
                if (accumCount != accum.Length) throw new Exception("wtf");
                data.AddRange(peekBuf);
                goto loop;
            }

            byte[] bufToLf = new byte[pos + 1];
            int readCount = await FtpSocket.ReceiveAsync(bufToLf, cancellationToken);
            if (readCount != bufToLf.Length) throw new Exception("wtf");

            data.AddRange(bufToLf);
            if (data.Count > 100_000) throw new ArgumentOutOfRangeException();

            string ascii = Encoding.ASCII.GetString(data.ToArray());
            yield return ascii.TrimEnd();

            data.Clear();
            goto loop;
        }
    }

    // TODO use Pipe
    // https://learn.microsoft.com/en-us/dotnet/standard/io/pipelines
    public async IAsyncEnumerable<string> ReadLineAsyncEnum(Encoding encoding, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int MaxReadSize = 512;

        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        var data = new ArrayBufferWriter<byte>();
        PartialSplitStatus split_status = new();
        int count;
        var stream = GetStream()!;

        do
        {
            var buf = new byte[MaxReadSize];
            count = await stream.ReadAsync(buf, cancellationToken);
            if (count == 0) break;
            data.Write(buf.AsSpan()[..count]);
            // UNDONE non mi chiude lui la connessione, devo processare codice 226 mi sa
            foreach (string line in SplitEncodePartial(data.WrittenSpan, encoding, split_status))
                yield return line;
        }
        while (count > 0);
    }

    public async Task<ICollection<string>> ReadLinesAsync(Encoding encoding, CancellationToken cancellationToken)
    {
        const int MaxReadSize = 512;

        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        int count;
        var data = new ArrayBufferWriter<byte>();
        var stream = GetStream()!;
        do
        {
            var buffer = new byte[MaxReadSize];
            count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            data.Write(buffer.AsSpan()[..count]);
        }
        while (count > 0);

        return SplitEncode(data.WrittenSpan, encoding);
    }

    public async Task WriteLineAsync(string buf, CancellationToken cancellationToken)
    {
        var data = Encoding.GetBytes($"{buf}\r\n");
        await _ftpStream!.WriteAsync(data, cancellationToken);
    }

    public void ResetTimeouts()
    {
        _ftpStream.ReadTimeout = Configuration.TimeoutSeconds * SecondsToMilli;
        _ftpStream.WriteTimeout = Configuration.TimeoutSeconds * SecondsToMilli;
    }

    public Task<IAsyncEnumerable<string>> SendCommandReaderAsync(FtpCommand command, CancellationToken token = default)
        => SendReadAsync(new FtpCommandEnvelope(command), token);

    public Task<FtpResponse> SendCommandReadFtpResponseAsync(FtpCommandEnvelope envelope, CancellationToken token = default)
    {
        string commandString = envelope.GetCommandString();
        return SendRead2Async(commandString, token);
    }

    public Task<IAsyncEnumerable<string>> SendReadAsync(FtpCommandEnvelope envelope, CancellationToken token = default)
    {
        string commandString = envelope.GetCommandString();
        return SendCommandReaderAsync(commandString, token);
    }

    public async Task<FtpResponse> SendRead2Async(string command, CancellationToken token = default)
    {
        await Semaphore.WaitAsync(token);

        try
        {
            if (SocketDataAvailable() is int size && size > 0)
            {
                var staleDataResult = await GetFtpResponseAsync(token);
                Logger?.LogWarning("[CoreFtp] Stale data on socket ({size}): {responseMessage}", size, staleDataResult.ResponseMessage);
            }

            string commandToPrint = command.StartsWith(FtpCommand.PASS.ToString())
                ? "PASS ***"
                : command;

            Logger?.LogDebug("[CoreFtp] Sending command: {commandToPrint}", commandToPrint);
            await WriteLineAsync(command, token);

            return await GetFtpResponseAsync(token);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<IAsyncEnumerable<string>> SendCommandReaderAsync(string command, CancellationToken token = default)
    {
        await Semaphore.WaitAsync(token);

        try
        {
            if (SocketDataAvailable() is int size && size > 0)
            {
                var staleDataResult = await GetFtpResponseAsync(token);
                Logger?.LogWarning("[CoreFtp] Stale data on socket ({size}): {responseMessage}", size, staleDataResult.ResponseMessage);
            }

            // TODO log write
            await WriteLineAsync(command, token);
            return await GetResponseReaderAsync(token);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public void SetTimeouts(int milliseconds)
    {
        _ftpStream.ReadTimeout = milliseconds;
        _ftpStream.WriteTimeout = milliseconds;
    }

    public int? SocketDataAvailable()
        => FtpSocket?.Available;

    private async Task ActivateEncryptionAsync()
    {
        if (!IsConnected)
            throw new InvalidOperationException("The FtpSocketStream object is not connected.");

        if (_ftpStream == null)
            throw new InvalidOperationException("The base network stream is null.");

        if (IsEncrypted)
            return;

        try
        {
            SslStream = new SslStream(_ftpStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
            await SslStream.AuthenticateAsClientAsync(Configuration.Host, Configuration.ClientCertificates, Configuration.SslProtocols, true);
        }
        catch (AuthenticationException authErr)
        {
            Logger?.LogError(authErr, "[CoreFtp] Could not activate encryption for the connection");
            throw;
        }
    }

    private async Task ConnectStreamAsync(string host, int port, CancellationToken token)
    {
        try
        {
            await Semaphore.WaitAsync(token);
            Logger?.LogDebug("[CoreFtp] Connecting stream on {host}:{port}", host, port);
            FtpSocket = await ConnectSocketAsync(host, port, token);
            if (FtpSocket == null) return;
            _ftpStream = new NetworkStream(FtpSocket);
            ResetTimeouts();
            LastActivity = DateTime.Now;

            if (IsDataConnection)
            {
                if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Explicit)
                    await ActivateEncryptionAsync();

                return;
            }

            if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Implicit)
                await ActivateEncryptionAsync();

            Logger?.LogDebug("[CoreFtp] Waiting for welcome message");

            while (true)
            {
                if (SocketDataAvailable() is > 0)
                {
                    var reader = await GetResponseReaderAsync(token);
                    _ = await FtpModelParser.ParseMotdAsync(reader);
                    return;
                }
                await Task.Delay(10, token);
            }
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private async Task<Socket?> ConnectSocketAsync(string host, int port, CancellationToken token)
    {
        try
        {
            Logger?.LogDebug("[CoreFtp] Connecting");
            var ipEndpoint = await DnsResolver.ResolveAsync(host, port, Configuration.IpVersion, token);
            if (ipEndpoint == null)
            {
                Logger?.LogWarning("[CoreFtp] WARNING endpoint was null for {host}:{port}", host, port);
                return null;
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = Configuration.TimeoutSeconds * SecondsToMilli
            };
            await socket.ConnectAsync(ipEndpoint);
            socket.LingerState = new LingerOption(true, 0);
            return socket;
        }
        catch (Exception socketErr)
        {
            Logger?.LogError(socketErr, "[CoreFtp] Could not to connect socket {host}:{port}", host, port);
            throw;
        }
    }

    [GeneratedRegex("^(?<statusCode>[0-9]{3}) (?<message>.*)$")]
    private static partial Regex CreateFtpRegex();

    private async Task EncryptExplicitly(CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] Encrypting explicitly");
        var response = await SendRead2Async("AUTH TLS", token);

        if (response.IsSuccess == false)
            throw new InvalidOperationException();

        await ActivateEncryptionAsync();
    }

    private async Task EncryptImplicitly(CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] Encrypting implicitly");
        await ActivateEncryptionAsync();

        var response = await GetFtpResponseAsync(token);
        if (!response.IsSuccess)
        {
            throw new IOException($"Could not securely connect to host {Configuration.Host}:{Configuration.Port}");
        }
    }

    private bool OnValidateCertificate(X509Certificate _, X509Chain __, SslPolicyErrors errors)
        => Configuration.IgnoreCertificateErrors || errors == SslPolicyErrors.None;

    private static ICollection<string> SplitEncode(ReadOnlySpan<byte> writtenSpan, Encoding encoding)
    {
        List<string> lines = new(1);
        int prev_pos = 0;

        do
        {
            int newline_pos = writtenSpan[prev_pos..].IndexOf((byte)'\n');
            if (newline_pos == -1)
            {
                if (writtenSpan.Length > 0) throw new InvalidOperationException("non-terminated data");
                break;
            }
            newline_pos += prev_pos;
            var token = writtenSpan[prev_pos..newline_pos].TrimEnd((byte)'\r');
            string encoded = encoding.GetString(token);
            lines.Add(encoded);
            prev_pos = newline_pos + 1;
        }
        while (prev_pos < writtenSpan.Length);

        return lines;
    }

    private static ICollection<string> SplitEncodePartial(ReadOnlySpan<byte> writtenSpan, Encoding encoding, PartialSplitStatus status)
    {
        List<string> lines = new(1);

        do
        {
            int newline_pos = writtenSpan[status.PreviousPosition..].IndexOf((byte)'\n');
            if (newline_pos == -1) break;
            newline_pos += status.PreviousPosition;
            var token = writtenSpan[status.PreviousPosition..newline_pos].TrimEnd((byte)'\r');
            string encoded = encoding.GetString(token);
            lines.Add(encoded);
            status.PreviousPosition = newline_pos + 1;
        }
        while (status.PreviousPosition < writtenSpan.Length);

        return lines;
    }

    private class PartialSplitStatus
    {
        public int PreviousPosition = 0;
    }
}
