using CoreFtp.Components.DirectoryListing;
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
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CoreFtp.Infrastructure.Stream;

public sealed partial class FtpControlStream : System.IO.Stream
{
    public override bool CanRead => NetworkStream != null && NetworkStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => NetworkStream != null && NetworkStream.CanWrite;
    public Encoding Encoding { get; set; } = Encoding.ASCII;
    public bool IsEncrypted => SslStream != null && SslStream.IsEncrypted;
    public override long Length => NetworkStream?.Length ?? 0;
    public ILogger Logger;
    public override long Position { get => NetworkStream?.Position ?? 0; set => throw new InvalidOperationException(); }

    protected System.IO.Stream? BaseStream;
    protected readonly FtpClientConfiguration Configuration;
    protected readonly IDnsResolver DnsResolver;
    protected DateTime LastActivity = DateTime.Now;
    protected System.IO.Stream NetworkStream => SslStream ?? BaseStream;
    protected readonly SemaphoreSlim ReceiveSemaphore = new(1, 1);
    protected readonly SemaphoreSlim Semaphore = new(1, 1);
    protected Socket? Socket;
    protected int SocketPollInterval { get; } = 15000;
    protected SslStream? SslStream { get; set; }
    protected static Regex FtpRegex = CreateFtpRegex();

    public bool IsConnected
    {
        get
        {
            try
            {
                if (Socket == null || !Socket.Connected || !CanRead || !CanWrite)
                {
                    Disconnect();
                    return false;
                }

                if (LastActivity.HasIntervalExpired(DateTime.Now, SocketPollInterval))
                {
                    Logger?.LogDebug("[CoreFtp] Polling connection");
                    if (Socket.Poll(500000, SelectMode.SelectRead) && Socket.Available == 0)
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
        await ConnectStreamAsync(token);

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
            BaseStream?.Dispose();
            SslStream?.Dispose();
            Socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception)
        {
            Logger?.LogError(exception, "[CoreFtp] Exception caught");
        }
        finally
        {
            Socket = null;
            BaseStream = null;
        }
    }

    public override void Flush()
    {
        if (!IsConnected)
            throw new InvalidOperationException("The FtpSocketStream object is not connected.");

        NetworkStream?.Flush();
    }

    public async Task<FtpResponse> GetResponseAsync(CancellationToken token = default)
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

            foreach (string? line in await ReadLinesAsync(Encoding, token))
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

    public async Task<System.IO.Stream> OpenDataStreamAsync(string host, int port, CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] FtpSocketStream: Opening datastream");
        var socketStream = new FtpControlStream(Configuration, DnsResolver) { Logger = Logger, IsDataConnection = true };
        await socketStream.ConnectStreamAsync(host, port, token);

        if (IsEncrypted)
        {
            await socketStream.ActivateEncryptionAsync();
        }
        return socketStream;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => NetworkStream.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin)
        => NetworkStream.Seek(offset, origin);

    public async Task<FtpResponse> SendCommandReadAsync(FtpCommand command, CancellationToken token = default)
        => await SendCommandReadAsync(new FtpCommandEnvelope(command), token);

    public async Task<FtpResponse> SendCommandReadAsync(FtpCommandEnvelope envelope, CancellationToken token = default)
    {
        string commandString = envelope.GetCommandString();
        return await SendReadAsync(commandString, token);
    }

    public async Task<FtpResponse> SendReadAsync(string command, CancellationToken token = default)
    {
        await Semaphore.WaitAsync(token);

        try
        {
            if (SocketDataAvailable())
            {
                var staleDataResult = await GetResponseAsync(token);
                Logger?.LogWarning("[CoreFtp] Stale data on socket {responseMessage}", staleDataResult.ResponseMessage);
            }

            string commandToPrint = command.StartsWith(FtpCommand.PASS.ToString())
                ? "PASS *****"
                : command;

            Logger?.LogDebug("[CoreFtp] Sending command: {commandToPrint}", commandToPrint);
            await WriteLineAsync(command, token);

            var response = await GetResponseAsync(token);
            return response;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public override void SetLength(long value) => throw new InvalidOperationException();

    public bool SocketDataAvailable() => (Socket?.Available ?? 0) > 0;

    public override void Write(byte[] buffer, int offset, int count)
        => throw new Exception("use async");

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await NetworkStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

    internal void ResetTimeouts()
    {
        BaseStream.ReadTimeout = Configuration.TimeoutSeconds * 1000;
        BaseStream.WriteTimeout = Configuration.TimeoutSeconds * 1000;
    }

    internal void SetTimeouts(int milliseconds)
    {
        BaseStream.ReadTimeout = milliseconds;
        BaseStream.WriteTimeout = milliseconds;
    }

    //protected string? ReadLine(Encoding encoding, CancellationToken token)
    //{
    //    if (encoding == null)
    //        throw new ArgumentNullException(nameof(encoding));
    //
    //    var data = new List<byte>();
    //    var buffer = new byte[1];
    //    string? line = null;
    //
    //    token.ThrowIfCancellationRequested();
    //
    //    while (Read(buffer, 0, buffer.Length) > 0)
    //    {
    //        token.ThrowIfCancellationRequested();
    //        data.Add(buffer[0]);
    //        if ((,r)buffer[0] != '\n')
    //            continue;
    //        line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
    //        break;
    //    }
    //
    //    return line;
    //}

    //private IEnumerable<string?> ReadLines(CancellationToken token)
    //{
    //    string? line;
    //    while ((line = ReadLine(Encoding, token)) != null)
    //    {
    //        yield return line;
    //    }
    //}

    protected async Task ActivateEncryptionAsync()
    {
        if (!IsConnected)
            throw new InvalidOperationException("The FtpSocketStream object is not connected.");

        if (BaseStream == null)
            throw new InvalidOperationException("The base network stream is null.");

        if (IsEncrypted)
            return;

        try
        {
            SslStream = new SslStream(BaseStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
            await SslStream.AuthenticateAsClientAsync(Configuration.Host, Configuration.ClientCertificates, Configuration.SslProtocols, true);
        }
        catch (AuthenticationException authErr)
        {
            Logger?.LogError(authErr, "[CoreFtp] Could not activate encryption for the connection");
            throw;
        }
    }

    protected async Task ConnectStreamAsync(CancellationToken token) => await ConnectStreamAsync(Configuration.Host, Configuration.Port, token);

    protected async Task ConnectStreamAsync(string host, int port, CancellationToken token)
    {
        try
        {
            await Semaphore.WaitAsync(token);
            Logger?.LogDebug("[CoreFtp] Connecting stream on {host}:{port}", host, port);
            Socket = await ConnectSocketAsync(host, port, token);
            BaseStream = new NetworkStream(Socket);
            ResetTimeouts();
            LastActivity = DateTime.Now;

            if (IsDataConnection)
            {
                if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Explicit)
                {
                    await ActivateEncryptionAsync();
                }

                return;
            }
            else
            {
                if (Configuration.ShouldEncrypt && Configuration.EncryptionType == FtpEncryption.Implicit)
                {
                    await ActivateEncryptionAsync();
                }
            }

            Logger?.LogDebug("[CoreFtp] Waiting for welcome message");

            while (true)
            {
                if (SocketDataAvailable())
                {
                    await GetResponseAsync(token);
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

    protected async Task<Socket> ConnectSocketAsync(string host, int port, CancellationToken token)
    {
        try
        {
            Logger?.LogDebug("[CoreFtp] Connecting");
            var ipEndpoint = await DnsResolver.ResolveAsync(host, port, Configuration.IpVersion, token);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = Configuration.TimeoutSeconds * 1000
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

    protected override void Dispose(bool disposing)
    {
        Logger?.LogTrace("[CoreFtp] {msg}", IsDataConnection ? "Disposing of data connection" : "Disposing of control connection");
        if (disposing) Disconnect();
        base.Dispose(disposing);
    }

    protected async Task EncryptExplicitly(CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] Encrypting explicitly");
        var response = await SendReadAsync("AUTH TLS", token);

        if (!response.IsSuccess)
            throw new InvalidOperationException();

        await ActivateEncryptionAsync();
    }

    protected async Task EncryptImplicitly(CancellationToken token)
    {
        Logger?.LogDebug("[CoreFtp] Encrypting implicitly");
        await ActivateEncryptionAsync();

        var response = await GetResponseAsync(token);
        if (!response.IsSuccess)
        {
            throw new IOException($"Could not securely connect to host {Configuration.Host}:{Configuration.Port}");
        }
    }

    protected async Task WriteLineAsync(string buf, CancellationToken cancellationToken)
    {
        var data = Encoding.GetBytes($"{buf}\r\n");
        await WriteAsync(data, cancellationToken);
    }

    protected async Task<ICollection<string>> ReadLinesAsync(Encoding encoding, CancellationToken cancellationToken)
    {
        const int MaxReadSize = 1024;

        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        int count;
        var data = new ArrayBufferWriter<byte>();
        do
        {
            var buffer = new byte[MaxReadSize];
            count = await ReadAsync(new Memory<byte>(buffer), cancellationToken);
            if (count == 0) break;
            data.Write(buffer.AsSpan()[..count]);
        } while (count == MaxReadSize);

        return DirectoryProviderBase.SplitEncode(data.WrittenSpan, encoding);
    }

    [GeneratedRegex("^(?<statusCode>[0-9]{3}) (?<message>.*)$")]
    private static partial Regex CreateFtpRegex();

    private bool OnValidateCertificate(X509Certificate _, X509Chain __, SslPolicyErrors errors)
        => Configuration.IgnoreCertificateErrors || errors == SslPolicyErrors.None;
}
