using CoreFtp.Components.DnsResolution;
using CoreFtp.Enum;
using CoreFtp.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CoreFtp.Infrastructure.Stream;

public sealed partial class FtpControlStream
{
    public Encoding Encoding { get; set; } = Encoding.ASCII;
    public bool IsEncrypted => _sslStream != null && _sslStream.IsEncrypted;

    private readonly FtpClientConfiguration _configuration;
    private readonly IDnsResolver _dnsResolver;
    private Socket? _ftpSocket;
    private NetworkStream? _ftpStream;
    private DateTime _lastActivity = DateTime.Now;
    private readonly ILogger? _logger;
    private readonly FtpModelParser _parser = new();
    private CancellationTokenSource _readerCancellation = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private SslStream? _sslStream;

    private const int SocketPollInterval = 15000;
    private const int SecondsToMilli = 1000;

    public bool IsConnected
    {
        get
        {
            try
            {
                if (_ftpSocket?.Connected != true || _ftpStream?.CanRead != true || _ftpStream?.CanWrite != true)
                {
                    Disconnect();
                    return false;
                }

                if (_lastActivity.HasIntervalExpired(DateTime.Now, SocketPollInterval))
                {
                    _logger?.LogDebug("[CoreFtp] Polling connection");
                    if (_ftpSocket?.Poll(500000, SelectMode.SelectRead) == true && _ftpSocket.Available == 0)
                    {
                        Disconnect();
                        return false;
                    }
                }
            }
            catch (SocketException socketException)
            {
                Disconnect();
                _logger?.LogError(socketException, "[CoreFtp] FtpSocketStream.IsConnected: Caught and discarded SocketException while testing for connectivity");
                return false;
            }
            catch (IOException ioException)
            {
                Disconnect();
                _logger?.LogError(ioException, "[CoreFtp] FtpSocketStream.IsConnected: Caught and discarded IOException while testing for connectivity");
                return false;
            }

            return true;
        }
    }

    public FtpControlStream(FtpClientConfiguration configuration, IDnsResolver dnsResolver, ILogger? logger)
    {
        _logger = logger;
        _logger?.LogDebug("[CoreFtp] Constructing new FtpSocketStream");
        _configuration = configuration;
        _dnsResolver = dnsResolver;
    }

    public async Task ClntAsync(string clientName, CancellationToken cancellationToken)
    {
        var clntCmd = new FtpCommandEnvelope(FtpCommand.CLNT, clientName);
        var resp = await SendAsync(clntCmd, cancellationToken);
        if (resp.Code.IsError()) throw new FtpException(resp.Message);
    }

    public async Task ConnectAsync(CancellationToken token = default)
    {
        if (false == await ConnectStreamAsync(_configuration.Host, _configuration.Port, token))
            return;

        if (false == IsConnected)
            return;

        if (_configuration.ShouldEncrypt || IsEncrypted)
        {
            if (_configuration.EncryptionType == FtpEncryption.Implicit)
                await EncryptImplicitly(token);

            if (_configuration.EncryptionType == FtpEncryption.Explicit)
                await EncryptExplicitly(token);
        }

        _readerCancellation = new();
        _ = ReadLoopAsync();

        var (Code, _, _) = await _parser.ResponseReader.ReadAsync(token); // TODO timeout
        if (Code.IsError())
            throw new IOException("Could not connect to the FTP server.");
        if (Code != CFtpStatusCode.Code220Motd)
            throw new InvalidOperationException("motd failed");

        _logger?.LogDebug("[CoreFTP] motd received");
    }

    public async Task<bool> CwdAsync(string directory, CancellationToken cancellationToken)
    {
        var resp = await SendAsync(new FtpCommandEnvelope(FtpCommand.CWD, directory), cancellationToken);
        if (resp.Code.IsError()) return false;
        return resp.Code == CFtpStatusCode.Code250FileActionOK;
    }

    public async Task<bool> DeleteAsync(string fileName, CancellationToken cancellationToken)
    {
        var cmd = new FtpCommandEnvelope(FtpCommand.DELE, fileName);
        var resp = await SendAsync(cmd, cancellationToken);
        if (resp.Code.IsError()) return false;
        return true;
    }

    public void Disconnect()
    {
        _logger?.LogTrace("[CoreFtp] Disconnecting");
        try
        {
            _readerCancellation.Cancel();
            _ftpStream?.Dispose();
            _sslStream?.Dispose();
            _ftpSocket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "[CoreFtp] Exception caught");
        }
        finally
        {
            _ftpSocket = null;
            _ftpStream = null;
        }
    }

    public void Dispose(bool disposing)
    {
        _logger?.LogTrace("[CoreFtp] Disposing of control connection");
        if (disposing) Disconnect();
    }

    public async Task<int?> EpsvAsync(CancellationToken cancellationToken)
    {
        var (Code, Message, _) = await SendAsync(FtpCommand.EPSV, cancellationToken);
        if (Code != CFtpStatusCode.COde229EnteringExtendedPassive) return null;

        var regex = FtpModelParser.CreateEpsvPortRegex();
        var match = regex.Match(Message);
        if (match.Success == false) return null;

        return int.Parse(match.Groups["PortNumber"].Value);
    }

    public System.IO.Stream GetStream()
        => _sslStream ?? (System.IO.Stream?)_ftpStream ?? throw new InvalidOperationException();

    public async Task<string[]> GetFeatsAsync(CancellationToken cancellationToken)
    {
        var (Code, _, AdditionalData) = await SendAsync(FtpCommand.FEAT, cancellationToken);
        if (Code.IsError())
        {
            _logger?.LogError("[CoreFtp] FEAT command unsuccessful");
            throw new Exception();
        }
        var features = AdditionalData!.Select(_ => _.Trim());
        return features.ToArray();
    }

    public async Task<(bool, Task?)> MlsdAsync(CancellationToken cancellation)
    {
        var resp = await SendAsync(new FtpCommandEnvelope(FtpCommand.MLSD), cancellation);
        if (resp.Code.IsError()) return (false, null);
        var ok = resp.Code is CFtpStatusCode.Code125DataAlreadyOpen or CFtpStatusCode.Code150OpeningData;
        return (ok, ok ? EndAsync(_parser.ResponseReader, cancellation) : null);

        static async Task EndAsync(
            System.Threading.Channels.ChannelReader<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> responseReader,
            CancellationToken cancellation)
        {
            var (Code, _, _) = await responseReader.ReadAsync(cancellation);
            if (Code.IsError()) throw new FtpException();
            if (Code != CFtpStatusCode.Code226ClosingData) throw new FtpException("expected code 226");
        }
    }

    public async Task<(bool, Task?)> ListAsync(DirSort? sortBy, CancellationToken cancellation)
    {
        string arguments = sortBy switch
        {
            DirSort.Alphabetical => "-1",
            DirSort.AlphabeticalReverse => "-r",
            DirSort.ModifiedTimestampReverse => "-t",
            _ => String.Empty,
        };
        var listCmd = new FtpCommandEnvelope(FtpCommand.LIST, arguments);
        var resp = await SendAsync(listCmd, cancellation);
        if (resp.Code.IsError()) return (false, null);
        // TODO ma non devo fare 125/150?
        return (true, EndAsync(_parser.ResponseReader, cancellation));

        static async Task EndAsync(
            System.Threading.Channels.ChannelReader<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> responseReader,
            CancellationToken cancellation)
        {
            var (Code, _, _) = await responseReader.ReadAsync(cancellation);
            if (Code.IsError()) throw new FtpException();
            if (Code != CFtpStatusCode.Code226ClosingData) throw new FtpException("expected code 226");
        }
    }

    public async Task MkdAsync(string directory, CancellationToken cancellationToken)
    {
        var mkdCmd = new FtpCommandEnvelope(FtpCommand.MKD, directory);
        var resp = await SendAsync(mkdCmd, cancellationToken);
        if (resp.Code.IsError()) throw new FtpException();
    }

    public async Task<int?> PasvAsync(CancellationToken cancellationToken)
    {
        var (Code, Message, _) = await SendAsync(FtpCommand.PASV, cancellationToken);
        if (Code != CFtpStatusCode.Code227EnteringPassive) return null;
        return Message.ExtractPasvPortNumber();
    }

    public async Task PbszAsync(CancellationToken cancellationToken)
    {
        var pbszCmd = new FtpCommandEnvelope(FtpCommand.PBSZ, "0");
        _ = await SendAsync(pbszCmd, cancellationToken);
    }

    public async Task ProtAsync(CancellationToken cancellationToken)
    {
        var protCmd = new FtpCommandEnvelope(FtpCommand.PROT, "P");
        _ = await SendAsync(protCmd, cancellationToken);
    }

    public async Task<string?> PwdAsync(CancellationToken cancellationToken)
    {
        var (Code, Message, _) = await SendAsync(FtpCommand.PWD, cancellationToken);
        if (Code.IsError()) return null;
        if (Code != CFtpStatusCode.Code257PathnameCreated) return null;

        var pwdParser = FtpModelParser.CreatePwdRegex();
        var parsed = pwdParser.Match(Message);
        if (parsed.Success == false) return null;

        return parsed.Groups["path"].Value;
    }

    public async Task QuitAsync(CancellationToken cancellationToken)
    {
        _ = await SendAsync(FtpCommand.QUIT, cancellationToken);
    }

    public async Task RenameAsync(string fromName, string toName, CancellationToken cancellationToken)
    {
        var rnfrCmd = new FtpCommandEnvelope(FtpCommand.RNFR, fromName);
        var renameFromResponse = await SendAsync(rnfrCmd, cancellationToken);
        if (renameFromResponse.Code != CFtpStatusCode.Code350FileCommandPending)
            throw new FtpException(renameFromResponse.Message);

        var rntoCmd = new FtpCommandEnvelope(FtpCommand.RNTO, toName);
        var renameToResponse = await SendAsync(rntoCmd, cancellationToken);
        if (renameToResponse.Code is not (CFtpStatusCode.Code250FileActionOK or CFtpStatusCode.Code226ClosingData))
            throw new FtpException(renameFromResponse.Message);
    }

    public void ResetTimeouts()
    {
        _ftpStream.ReadTimeout = _configuration.TimeoutSeconds * SecondsToMilli;
        _ftpStream.WriteTimeout = _configuration.TimeoutSeconds * SecondsToMilli;
    }

    public async Task RetrAsync(string fileName, CancellationToken cancellationToken)
    {
        var ftpCmd = new FtpCommandEnvelope(FtpCommand.RETR, fileName);
        var (Code, Message, _) = await SendAsync(ftpCmd, cancellationToken);
        if (Code.IsError()) throw new FtpException();
        if (Code is not (CFtpStatusCode.Code125DataAlreadyOpen or CFtpStatusCode.Code150OpeningData or CFtpStatusCode.Code226ClosingData))
            throw new FtpException(Message);
    }

    public async Task<RmdResult> RmdAsync(string directory, CancellationToken cancellationToken)
    {
        var cmd = new FtpCommandEnvelope(FtpCommand.RMD, directory);
        (var Code, string Message, _) = await SendAsync(cmd, cancellationToken);
        if (Code.IsError()) return RmdResult.Error;

        switch (Code)
        {
            case CFtpStatusCode.Code200CommandOK:
            case CFtpStatusCode.Code250FileActionOK:
                return RmdResult.Ok;

            case CFtpStatusCode.Code550ActionNotTakenFileUnavailable:
                return RmdResult.NotEmpty;

            default:
                throw new FtpException(Message);
        }
    }

    public async Task<bool> SendOptsAsync(string optsArg, CancellationToken cancellation)
    {
        var (Code, _, _) = await SendAsync($"OPTS {optsArg}", cancellation);
        if (Code.IsError()) return false;
        // 200 Always in UTF8 mode.
        return Code == CFtpStatusCode.Code200CommandOK;
    }

    public async Task<bool> SendPassAsync(string pass, CancellationToken cancellation)
    {
        var (Code, _, _) = await SendAsync(new FtpCommandEnvelope(FtpCommand.PASS, pass), cancellation, "PASS ***");
        if (Code.IsError()) return false;
        if (Code != CFtpStatusCode.Code230LoggedInProceed) return false;
        return true;
    }

    public async Task<bool> SendUserAsync(string user, CancellationToken cancellation)
    {
        var (Code, _, _) = await SendAsync(new FtpCommandEnvelope(FtpCommand.USER, user), cancellation);
        if (Code.IsError()) return false;
        return Code is CFtpStatusCode.Code331SendPasswordCommand or CFtpStatusCode.Code220Motd or CFtpStatusCode.Code230LoggedInProceed;
    }

    public async Task<long> SizeAsync(string fileName, CancellationToken cancellationToken)
    {
        var sizeCmd = new FtpCommandEnvelope(FtpCommand.SIZE, fileName);
        var (Code, Message, _) = await SendAsync(sizeCmd, cancellationToken);
        if (Code.IsError() || Code != CFtpStatusCode.Code213FileStatus)
            throw new FtpException(Message);
        return long.Parse(Message);
    }

    public async Task TypeAsync(FtpTransferMode transferMode, char secondType)
    {
        var typeCmd = new FtpCommandEnvelope(
            FtpCommand.TYPE,
            secondType != '\0'
                ? $"{(char)transferMode} {secondType}"
                : $"{(char)transferMode}");
        var (Code, Message, _) = await SendAsync(typeCmd);
        if (Code.IsError() || Code != CFtpStatusCode.Code200CommandOK) throw new FtpException(Message);
    }

    private Task<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> SendAsync(
        FtpCommand command,
        CancellationToken token = default,
        string? forceLog = null)
        => SendAsync(new FtpCommandEnvelope(command), token, forceLog);

    private Task<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> SendAsync(
        FtpCommandEnvelope envelope,
        CancellationToken token = default,
        string? forceLog = null)
    {
        string commandString = envelope.GetCommandString();
        return SendAsync(commandString, token, forceLog);
    }

    private async Task<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> SendAsync(
        string command,
        CancellationToken token = default,
        string? forceLog = null)
    {
        await _sendSemaphore.WaitAsync(token);

        try
        {
            _logger?.LogDebug("[CoreFtp] Sending command: {commandToPrint}", forceLog ?? command);
            await WriteLineAsync(command, token);
            return await _parser.ResponseReader.ReadAsync(token);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    public void SetTimeouts(int milliseconds)
    {
        _ftpStream.ReadTimeout = milliseconds;
        _ftpStream.WriteTimeout = milliseconds;
    }

    public int? SocketDataAvailable()
        => _ftpSocket?.Available;

    public async Task StorAsync(string fileName, CancellationToken cancellationToken)
    {
        var ftpCmd = new FtpCommandEnvelope(FtpCommand.STOR, fileName);
        var (Code, Message, _) = await SendAsync(ftpCmd, cancellationToken);
        if (Code.IsError()) throw new FtpException();
        if (Code is not (CFtpStatusCode.Code125DataAlreadyOpen or CFtpStatusCode.Code150OpeningData or CFtpStatusCode.Code226ClosingData))
            throw new FtpException(Message);
    }

    public async Task WaitEndDataStreamAsync()
    {
        var (Code, Message, _) = await _parser.ResponseReader.ReadAsync();
        if (Code.IsError())
            throw new FtpException(Message);
        if (Code != CFtpStatusCode.Code226ClosingData)
            throw new FtpException(Message);
    }

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
            _sslStream = new SslStream(_ftpStream, true, (sender, certificate, chain, sslPolicyErrors) => OnValidateCertificate(certificate, chain, sslPolicyErrors));
            await _sslStream.AuthenticateAsClientAsync(_configuration.Host, _configuration.ClientCertificates, _configuration.SslProtocols, checkCertificateRevocation: true);
        }
        catch (AuthenticationException authErr)
        {
            _logger?.LogError(authErr, "[CoreFtp] Could not activate encryption for the connection");
            throw;
        }
    }

    private async Task<Socket?> ConnectSocketAsync(string host, int port, CancellationToken token)
    {
        try
        {
            _logger?.LogDebug("[CoreFtp] Connecting");
            var ipEndpoint = await _dnsResolver.ResolveAsync(host, port, _configuration.IpVersion, token);
            if (ipEndpoint == null)
            {
                _logger?.LogWarning("[CoreFtp] WARNING endpoint was null for {host}:{port}", host, port);
                return null;
            }
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ReceiveTimeout = _configuration.TimeoutSeconds * SecondsToMilli
            };
            await socket.ConnectAsync(ipEndpoint);
            socket.LingerState = new LingerOption(true, 0);
            return socket;
        }
        catch (Exception socketErr)
        {
            _logger?.LogError(socketErr, "[CoreFtp] Could not to connect socket {host}:{port}", host, port);
            throw;
        }
    }

    private async Task<bool> ConnectStreamAsync(string host, int port, CancellationToken token)
    {
        _logger?.LogDebug("[CoreFtp] Connecting stream on {host}:{port}", host, port);
        _ftpSocket = await ConnectSocketAsync(host, port, token);
        if (_ftpSocket == null) return false;
        _ftpStream = new NetworkStream(_ftpSocket);
        ResetTimeouts();
        _lastActivity = DateTime.Now;

        if (_configuration.ShouldEncrypt && _configuration.EncryptionType == FtpEncryption.Implicit)
            await ActivateEncryptionAsync();

        _logger?.LogDebug("[CoreFtp] Waiting for welcome message");
        return true;
    }

    private async Task EncryptExplicitly(CancellationToken token)
    {
        _logger?.LogDebug("[CoreFtp] Encrypting explicitly");
        var response = await SendAsync("AUTH TLS", token);

        if (response.Code.IsError())
            throw new InvalidOperationException();

        await ActivateEncryptionAsync();
    }

    private async Task EncryptImplicitly(CancellationToken token)
    {
        _logger?.LogDebug("[CoreFtp] Encrypting implicitly");
        await ActivateEncryptionAsync();
        var response = await _parser.ResponseReader.ReadAsync(token);

        if (response.Code.IsError())
            throw new IOException($"Could not securely connect to host {_configuration.Host}:{_configuration.Port}");
    }

    private bool OnValidateCertificate(X509Certificate _, X509Chain __, SslPolicyErrors errors)
        => _configuration.IgnoreCertificateErrors || errors == SslPolicyErrors.None;

    private async IAsyncEnumerable<string> ReadLineAsyncEnum(Encoding encoding, [EnumeratorCancellation] CancellationToken cancellationToken)
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
            foreach (string line in SplitEncodePartial(data.WrittenSpan, encoding, split_status))
                yield return line;
        }
        while (count > 0);
    }

    private async Task ReadLoopAsync()
    {
        await foreach (string controlReponse in ReadLineAsyncEnum(Encoding, _readerCancellation.Token))
        {
            _lastActivity = DateTime.Now;
            _logger?.LogDebug("[CoreFtp] data: {line}", controlReponse);
            await _parser.ParseAndSignalAsync(controlReponse, _readerCancellation.Token);
        }
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

    private async Task WriteLineAsync(string buf, CancellationToken cancellationToken)
    {
        var data = Encoding.GetBytes($"{buf}\r\n");
        await _ftpStream!.WriteAsync(data, cancellationToken);
    }

    private sealed class PartialSplitStatus
    {
        public int PreviousPosition = 0;
    }
}
