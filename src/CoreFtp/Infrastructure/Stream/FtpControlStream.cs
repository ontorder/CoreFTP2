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

public class FtpControlStream : System.IO.Stream
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

    internal bool IsDataConnection { get; set; }

    internal void SetTimeouts(int milliseconds)
    {
        BaseStream.ReadTimeout = milliseconds;
        BaseStream.WriteTimeout = milliseconds;
    }

    internal void ResetTimeouts()
    {
        BaseStream.ReadTimeout = Configuration.TimeoutSeconds * 1000;
        BaseStream.WriteTimeout = Configuration.TimeoutSeconds * 1000;
    }

    public FtpControlStream(FtpClientConfiguration configuration, IDnsResolver dnsResolver)
    {
        Logger?.LogDebug("Constructing new FtpSocketStream");
        Configuration = configuration;
        DnsResolver = dnsResolver;
    }

    public bool IsConnected
    {
        get
        {
            try
            {
                if (Socket == null || !Socket.Connected || (!CanRead || !CanWrite))
                {
                    Disconnect();
                    return false;
                }

                if (LastActivity.HasIntervalExpired(DateTime.Now, SocketPollInterval))
                {
                    Logger?.LogDebug("Polling connection");
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
                Logger?.LogError(socketException, "FtpSocketStream.IsConnected: Caught and discarded SocketException while testing for connectivity");
                return false;
            }
            catch (IOException ioException)
            {
                Disconnect();
                Logger?.LogError(ioException, $"FtpSocketStream.IsConnected: Caught and discarded IOException while testing for connectivity");
                return false;
            }

            return true;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => NetworkStream?.Read(buffer, offset, count) ?? 0;

    public override void Write(byte[] buffer, int offset, int count) => NetworkStream?.Write(buffer, offset, count);

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await NetworkStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException();

    public override void Flush()
    {
        if (!IsConnected)
            throw new InvalidOperationException("The FtpSocketStream object is not connected.");

        NetworkStream?.Flush();
    }

    public override void SetLength(long value) => throw new InvalidOperationException();

    public async Task ConnectAsync(CancellationToken token = default)
    {
        await ConnectStreamAsync(token);

        if (!Configuration.ShouldEncrypt)
            return;

        if (!IsConnected || IsEncrypted)
            return;

        if (Configuration.EncryptionType == FtpEncryption.Implicit)
            await EncryptImplicitly(token);

        if (Configuration.EncryptionType == FtpEncryption.Explicit)
            await EncryptExplicitly(token);
    }


    protected async Task WriteLineAsync(string buf, CancellationToken cancellationToken)
    {
        var data = Encoding.GetBytes($"{buf}\r\n");
        await WriteAsync(data, cancellationToken);
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
    //        if ((char)buffer[0] != '\n')
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

    public bool SocketDataAvailable() => (Socket?.Available ?? 0) > 0;

    public async Task<FtpResponse> SendCommandAsync(FtpCommand command, CancellationToken token = default)
    {
        return await SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = command
        }, token);
    }

    public async Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default)
    {
        string commandString = envelope.GetCommandString();
        return await SendCommandAsync(commandString, token);
    }

    public async Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default)
    {
        await Semaphore.WaitAsync(token);

        try
        {
            if (SocketDataAvailable())
            {
                var staleDataResult = await GetResponseAsync(token);
                Logger?.LogWarning("Stale data on socket {responseMessage}", staleDataResult.ResponseMessage);
            }

            string commandToPrint = command.StartsWith(FtpCommand.PASS.ToString())
                ? "PASS *****"
                : command;

            Logger?.LogDebug("[FtpClient] Sending command: {commandToPrint}", commandToPrint);
            await WriteLineAsync(command, token);

            var response = await GetResponseAsync(token);
            return response;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    public async Task<FtpResponse> GetResponseAsync(CancellationToken token = default)
    {
        Logger?.LogTrace("Getting Response");

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
                Logger?.LogDebug("{line}", line);
                data.Add(line);

                Match match;

                if (false == (match = Regex.Match(line, "^(?<statusCode>[0-9]{3}) (?<message>.*)$")).Success)
                    continue;
                Logger?.LogTrace("Finished receiving message");
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
        Logger?.LogDebug("[FtpSocketStream] Opening datastream");
        var socketStream = new FtpControlStream(Configuration, DnsResolver) { Logger = Logger, IsDataConnection = true };
        await socketStream.ConnectStreamAsync(host, port, token);

        if (IsEncrypted)
        {
            await socketStream.ActivateEncryptionAsync();
        }
        return socketStream;
    }

    protected async Task ConnectStreamAsync(CancellationToken token) => await ConnectStreamAsync(Configuration.Host, Configuration.Port, token);

    protected async Task ConnectStreamAsync(string host, int port, CancellationToken token)
    {
        try
        {
            await Semaphore.WaitAsync(token);
            Logger?.LogDebug("Connecting stream on {host}:{port}", host, port);
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

            Logger?.LogDebug("Waiting for welcome message");

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
            Logger?.LogDebug("Connecting");
            var ipEndpoint = await DnsResolver.ResolveAsync(host, port, Configuration.IpVersion, token);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = Configuration.TimeoutSeconds * 1000
            };
            //                socket.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true );
            //                socket.SetSocketOption( SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true );
            socket.Connect(ipEndpoint);
            socket.LingerState = new LingerOption(true, 0);
            return socket;
        }
        catch (Exception exception)
        {
            Logger?.LogError(exception, "Could not to connect socket {host}:{port}", host, port);
            throw;
        }
    }

    protected async Task EncryptImplicitly(CancellationToken token)
    {
        Logger?.LogDebug("Encrypting implicitly");
        await ActivateEncryptionAsync();

        var response = await GetResponseAsync(token);
        if (!response.IsSuccess)
        {
            throw new IOException($"Could not securely connect to host {Configuration.Host}:{Configuration.Port}");
        }
    }

    protected async Task EncryptExplicitly(CancellationToken token)
    {
        Logger?.LogDebug("Encrypting explicitly");
        var response = await SendCommandAsync("AUTH TLS", token);

        if (!response.IsSuccess)
            throw new InvalidOperationException();

        await ActivateEncryptionAsync();
    }

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
            Logger?.LogError(authErr, "Could not activate encryption for the connection");
            throw;
        }
    }

    private bool OnValidateCertificate(X509Certificate _, X509Chain __, SslPolicyErrors errors)
        => Configuration.IgnoreCertificateErrors || errors == SslPolicyErrors.None;

    public void Disconnect()
    {
        Logger?.LogTrace("Disconnecting");
        try
        {
            BaseStream?.Dispose();
            SslStream?.Dispose();
            Socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception)
        {
            Logger?.LogError(exception, "Exception caught");
        }
        finally
        {
            Socket = null;
            BaseStream = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        Logger?.LogTrace(IsDataConnection ? "Disposing of data connection" : "Disposing of control connection");
        if (disposing) Disconnect();
        base.Dispose(disposing);
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
}
