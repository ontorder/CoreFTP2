using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreFtp.Components.DirectoryListing;
using CoreFtp.Components.DnsResolution;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using CoreFtp.Infrastructure.Stream;
using Microsoft.Extensions.Logging;

#nullable enable

namespace CoreFtp;

public sealed class FtpClient : IFtpClient
{
    public FtpClientConfiguration Configuration { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public bool IsConnected => ControlStream != null && ControlStream.IsConnected;
    public bool IsEncrypted => ControlStream != null && ControlStream.IsEncrypted;
    public ILogger Logger { private get => _logger; set => (_logger, ControlStream.Logger) = (value, value); }
    public string WorkingDirectory { get; private set; } = "/";

    internal FtpControlStream ControlStream { get; private set; }
    internal readonly SemaphoreSlim DataSocketSemaphore = new(1, 1);
    internal IEnumerable<string> Features { get; private set; }

    private Stream _dataStream;
    private IDirectoryProvider _directoryProvider;
    private ILogger _logger;

    public FtpClient(FtpClientConfiguration configuration) => Configure(configuration);

    public void Configure(FtpClientConfiguration configuration)
    {
        Configuration = configuration;

        if (configuration.Host == null)
            throw new ArgumentNullException(nameof(configuration.Host));

        if (Uri.IsWellFormedUriString(configuration.Host, UriKind.Absolute))
        {
            configuration.Host = new Uri(configuration.Host).Host;
        }

        ControlStream = new FtpControlStream(Configuration, new DnsResolver());
        Configuration.BaseDirectory = $"/{Configuration.BaseDirectory.TrimStart('/')}";
    }

    /// <summary>
    /// Changes the working directory to the given value for the current session
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    public async Task ChangeWorkingDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        Logger?.LogTrace("[FtpClient] changing directory to {directory}", directory);
        if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
            throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was incorrect");

        EnsureLoggedIn();

        var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.CWD,
            Data = directory
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new FtpException(response.ResponseMessage);

        var pwdResponse = await ControlStream.SendCommandAsync(FtpCommand.PWD, cancellationToken);

        if (!response.IsSuccess)
            throw new FtpException(response.ResponseMessage);

        if (pwdResponse.ResponseMessage.Contains(':') == false)
        {
            _logger.LogWarning("[FtpClient] change directory failed? '{resp}'", pwdResponse.ResponseMessage);
            throw new Exception($"ftp response '{pwdResponse.ResponseMessage}' has no ':'");
        }
        WorkingDirectory = pwdResponse.ResponseMessage.Split('"')[1];
    }

    /// <summary>
    /// Closes the write stream and associated socket (if open),
    /// </summary>
    /// <param name="ctsToken"></param>
    /// <returns></returns>
    public async Task CloseFileDataStreamAsync(CancellationToken ctsToken = default)
    {
        Logger?.LogTrace("[FtpClient] Closing write file stream");
        _dataStream.Dispose();

        if (ControlStream != null)
            await ControlStream.GetResponseAsync(ctsToken);
    }

    /// <summary>
    /// Creates a directory on the FTP Server
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    public async Task CreateDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
            throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was not valid");

        Logger?.LogDebug("[FtpClient] Creating directory {directory}", directory);
        EnsureLoggedIn();
        await CreateDirectoryStructureRecursively(directory.Split('/'), directory.StartsWith("/"), cancellationToken);
    }

    /// <summary>
    /// Deletes the given directory from the FTP server
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    public async Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
            throw new ArgumentOutOfRangeException(nameof(directory), "Directory supplied was not valid");

        if (directory == "/")
            return;

        Logger?.LogDebug("[FtpClient] Deleting directory {directory}", directory);

        EnsureLoggedIn();

        var rmdResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.RMD,
            Data = directory
        }, cancellationToken);

        switch (rmdResponse.FtpStatusCode)
        {
            case FtpStatusCode.CommandOK:
            case FtpStatusCode.FileActionOK:
                return;

            case FtpStatusCode.ActionNotTakenFileUnavailable:
                await DeleteNonEmptyDirectory(directory, cancellationToken);
                return;

            default:
                throw new FtpException(rmdResponse.ResponseMessage);
        }
    }

    /// <summary>
    /// Lists all directories in the current working directory
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task DeleteFileAsync(string fileName, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        Logger?.LogDebug("[FtpClient] Deleting file {fileName}", fileName);
        var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.DELE,
            Data = fileName
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new FtpException(response.ResponseMessage);
    }

    /// <summary>
    /// Deletes the given directory from the FTP server
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    private async Task DeleteNonEmptyDirectory(string directory, CancellationToken cancellationToken)
    {
        await ChangeWorkingDirectoryAsync(directory, cancellationToken);

        var allNodes = await ListAllAsync(cancellationToken);

        foreach (var file in allNodes.Where(x => x.NodeType == FtpNodeType.File))
        {
            await DeleteFileAsync(file.Name, cancellationToken);
        }

        foreach (var dir in allNodes.Where(x => x.NodeType == FtpNodeType.Directory))
        {
            await DeleteDirectoryAsync(dir.Name, cancellationToken);
        }

        await ChangeWorkingDirectoryAsync("..", cancellationToken);
        await DeleteDirectoryAsync(directory, cancellationToken);
    }

    /// <summary>
    /// Determines the file size of the given file
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task<long> GetFileSizeAsync(string fileName, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        Logger?.LogDebug("[FtpClient] Getting file size for {fileName}", fileName);
        var sizeResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.SIZE,
            Data = fileName
        }, cancellationToken);

        if (sizeResponse.FtpStatusCode != FtpStatusCode.FileStatus)
            throw new FtpException(sizeResponse.ResponseMessage);

        long fileSize = long.Parse(sizeResponse.ResponseMessage);
        return fileSize;
    }

    /// <summary>
    /// Lists all files in the current working directory
    /// </summary>
    /// <returns></returns>
    public async Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLoggedIn();
            Logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
            return await _directoryProvider.ListAllAsync(cancellationToken);
        }
        finally
        {
            await ControlStream.GetResponseAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Lists all files in the current working directory
    /// </summary>
    /// <returns></returns>
    public async Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLoggedIn();
            Logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
            return await _directoryProvider.ListFilesAsync(sortBy, cancellationToken);
        }
        finally
        {
            await ControlStream.GetResponseAsync(cancellationToken);
        }
    }

    public async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLoggedIn();
            Logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
            await foreach (var file in _directoryProvider.ListFilesAsyncEnum(sortBy, cancellationToken))
                yield return file;
        }
        finally
        {
            await ControlStream.GetResponseAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Lists all directories in the current working directory
    /// </summary>
    /// <returns></returns>
    public async Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLoggedIn();
            Logger?.LogDebug("[FtpClient] Listing directories in {WorkingDirectory}", WorkingDirectory);
            return await _directoryProvider.ListDirectoriesAsync(cancellationToken);
        }
        finally
        {
            await ControlStream.GetResponseAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Attempts to log the user in to the FTP Server
    /// </summary>
    /// <returns></returns>
    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            await LogOutAsync(cancellationToken);

        string username = Configuration.Username.IsNullOrWhiteSpace()
            ? Constants.ANONYMOUS_USER
            : Configuration.Username;

        await ControlStream.ConnectAsync(cancellationToken);

        var usrResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.USER,
            Data = username
        }, cancellationToken);

        await BailIfResponseNotAsync(usrResponse, cancellationToken, FtpStatusCode.SendUserCommand, FtpStatusCode.SendPasswordCommand, FtpStatusCode.LoggedInProceed);

        var passResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.PASS,
            Data = username != Constants.ANONYMOUS_USER ? Configuration.Password : string.Empty
        }, cancellationToken);

        await BailIfResponseNotAsync(passResponse, cancellationToken, FtpStatusCode.LoggedInProceed);
        IsAuthenticated = true;

        if (ControlStream.IsEncrypted)
        {
            await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.PBSZ,
                Data = "0"
            }, cancellationToken);

            await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.PROT,
                Data = "P"
            }, cancellationToken);
        }

        Features = await DetermineFeaturesAsync(cancellationToken);
        _directoryProvider = DetermineDirectoryProvider();
        await EnableUTF8IfPossible();
        await SetTransferMode(Configuration.Mode, Configuration.ModeSecondType);

        if (Configuration.BaseDirectory != "/")
        {
            await CreateDirectoryAsync(Configuration.BaseDirectory, cancellationToken);
        }

        await ChangeWorkingDirectoryAsync(Configuration.BaseDirectory, cancellationToken);
    }

    /// <summary>
    ///     Attemps to log the user out asynchronously, sends the QUIT command and terminates the command socket.
    /// </summary>
    public async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        await IgnoreStaleData(cancellationToken);
        if (!IsConnected)
            return;

        Logger?.LogTrace("[FtpClient] Logging out");
        await ControlStream.SendCommandAsync(FtpCommand.QUIT, cancellationToken);
        ControlStream.Disconnect();
        IsAuthenticated = false;
    }

    /// <summary>
    /// Provides a stream which contains the data of the given filename on the FTP server
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task<Stream> OpenFileReadStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        Logger?.LogDebug("[FtpClient] Opening file read stream for {fileName}", fileName);
        return new FtpDataStream(await OpenFileStreamAsync(fileName, FtpCommand.RETR, cancellationToken), this, Logger);
    }

    /// <summary>
    /// Provides a stream which can be written to
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task<Stream> OpenFileWriteStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        string filePath = WorkingDirectory.CombineAsUriWith(fileName);
        Logger?.LogDebug("[FtpClient] Opening file read stream for {filePath}", filePath);
        var segments = filePath
            .Split('/')
            .Where(x => !x.IsNullOrWhiteSpace())
            .ToList();
        await CreateDirectoryStructureRecursively(segments.Take(segments.Count - 1).ToArray(), filePath.StartsWith("/"), cancellationToken);
        return new FtpDataStream(await OpenFileStreamAsync(filePath, FtpCommand.STOR, cancellationToken), this, Logger);
    }

    /// <summary>
    /// Renames a file on the FTP server
    /// </summary>
    /// <param name="from"></param>
    /// <param name="to"></param>
    /// <returns></returns>
    public async Task RenameAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        Logger?.LogDebug("[FtpClient] Renaming from {from}, to {to}", from, to);
        var renameFromResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.RNFR,
            Data = from
        }, cancellationToken);

        if (renameFromResponse.FtpStatusCode != FtpStatusCode.FileCommandPending)
            throw new FtpException(renameFromResponse.ResponseMessage);

        var renameToResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.RNTO,
            Data = to
        }, cancellationToken);

        if (renameToResponse.FtpStatusCode != FtpStatusCode.FileActionOK && renameToResponse.FtpStatusCode != FtpStatusCode.ClosingData)
            throw new FtpException(renameFromResponse.ResponseMessage);
    }

    /// <summary>
    /// Informs the FTP server of the client being used
    /// </summary>
    /// <param name="clientName"></param>
    /// <returns></returns>
    public async Task<FtpResponse> SetClientNameAsync(string clientName, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        Logger?.LogDebug("[FtpClient] Setting client name to {clientName}", clientName);

        return await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.CLNT,
            Data = clientName
        }, cancellationToken);
    }

    /// <summary>
    /// Determines the file size of the given file
    /// </summary>
    /// <param name="transferMode"></param>
    /// <param name="secondType"></param>
    /// <returns></returns>
    public async Task SetTransferMode(FtpTransferMode transferMode, char secondType = '\0')
    {
        EnsureLoggedIn();
        Logger?.LogTrace("[FtpClient] Setting transfer mode {transferMode}, {secondType}", transferMode, secondType);
        var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = FtpCommand.TYPE,
            Data = secondType != '\0'
                ? $"{(char)transferMode} {secondType}"
                : $"{(char)transferMode}"
        });

        if (!response.IsSuccess)
            throw new FtpException(response.ResponseMessage);
    }

    public async Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default) => await ControlStream.SendCommandAsync(envelope, token);

    public async Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default) => await ControlStream.SendCommandAsync(command, token);

    /// <summary>
    /// Ignore any stale data we may have waiting on the stream
    /// </summary>
    /// <returns></returns>
    public void Dispose()
    {
        Logger?.LogDebug("Disposing of FtpClient");
        Task.WaitAny(LogOutAsync(default));
        ControlStream?.Dispose();
        DataSocketSemaphore?.Dispose();
    }

    private async Task IgnoreStaleData(CancellationToken cancellationToken)
    {
        if (IsConnected && ControlStream.SocketDataAvailable())
        {
            var staleData = await ControlStream.GetResponseAsync(cancellationToken);
            Logger?.LogWarning("Stale data detected: {msg}", staleData.ResponseMessage);
        }
    }

    /// <summary>
    /// Determines the type of directory listing the FTP server will return, and set the appropriate parser
    /// </summary>
    /// <returns></returns>
    private IDirectoryProvider DetermineDirectoryProvider()
    {
        Logger?.LogTrace("[FtpClient] Determining directory provider");
        if (this.UsesMlsd())
            return new MlsdDirectoryProvider(this, Logger, Configuration);

        return new ListDirectoryProvider(this, Logger, Configuration);
    }

    private async Task<IEnumerable<string>> DetermineFeaturesAsync(CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        Logger?.LogTrace("[FtpClient] Determining features");
        var response = await ControlStream.SendCommandAsync(FtpCommand.FEAT, cancellationToken);

        if (response.FtpStatusCode == FtpStatusCode.CommandSyntaxError || response.FtpStatusCode == FtpStatusCode.CommandNotImplemented)
            return Enumerable.Empty<string>();

        var features = response.Data
            .Where(x => !x.StartsWith(((int)FtpStatusCode.SystemHelpReply).ToString()) && !x.IsNullOrWhiteSpace())
            .Select(x => x.Replace(Constants.CARRIAGE_RETURN, string.Empty).Trim())
            .ToList();

        return features;
    }

    /// <summary>
    /// Creates a directory structure recursively given a path
    /// </summary>
    /// <param name="directories"></param>
    /// <param name="isRootedPath"></param>
    /// <returns></returns>
    private async Task CreateDirectoryStructureRecursively(IReadOnlyCollection<string> directories, bool isRootedPath, CancellationToken cancellationToken)
    {
        Logger?.LogDebug("[FtpClient] Creating directory structure recursively {dirs}", string.Join("/", directories));
        string originalPath = WorkingDirectory;

        if (isRootedPath && directories.Any())
            await ChangeWorkingDirectoryAsync("/", cancellationToken);

        if (!directories.Any())
            return;

        if (directories.Count == 1)
        {
            await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.MKD,
                Data = directories.First()
            }, cancellationToken);

            await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
            return;
        }

        foreach (string directory in directories)
        {
            if (directory.IsNullOrWhiteSpace())
                continue;

            var response = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.CWD,
                Data = directory
            }, cancellationToken);

            if (response.FtpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                continue;

            await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.MKD,
                Data = directory
            }, cancellationToken);
            await ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.CWD,
                Data = directory
            }, cancellationToken);
        }

        await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
    }

    /// <summary>
    /// Opens a filestream to the given filename
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="command"></param>
    /// <returns></returns>
    private async Task<Stream> OpenFileStreamAsync(string fileName, FtpCommand command, CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        Logger?.LogDebug("[FtpClient] Opening filestream for {fileName}, {command}", fileName, command);
        _dataStream = await ConnectDataStreamAsync(cancellationToken);

        var retrResponse = await ControlStream.SendCommandAsync(new FtpCommandEnvelope
        {
            FtpCommand = command,
            Data = fileName
        }, cancellationToken);

        if ((retrResponse.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) &&
             (retrResponse.FtpStatusCode != FtpStatusCode.OpeningData) &&
             (retrResponse.FtpStatusCode != FtpStatusCode.ClosingData))
            throw new FtpException(retrResponse.ResponseMessage);

        return _dataStream;
    }

    /// <summary>
    /// Checks if the command socket is open and that an authenticated session is active
    /// </summary>
    private void EnsureLoggedIn()
    {
        if (!IsConnected || !IsAuthenticated)
            throw new FtpException("User must be logged in");
    }

    /// <summary>
    /// Produces a data socket using Passive (PASV) or Extended Passive (EPSV) mode
    /// </summary>
    /// <returns></returns>
    internal async Task<Stream> ConnectDataStreamAsync(CancellationToken cancellationToken)
    {
        Logger?.LogTrace("[FtpClient] Connecting to a data socket");

        var epsvResult = await ControlStream.SendCommandAsync(FtpCommand.EPSV, cancellationToken);

        int? passivePortNumber;
        if (epsvResult.FtpStatusCode == FtpStatusCode.EnteringExtendedPassive)
        {
            passivePortNumber = epsvResult.ResponseMessage.ExtractEpsvPortNumber();
        }
        else
        {
            // EPSV failed - try regular PASV
            var pasvResult = await ControlStream.SendCommandAsync(FtpCommand.PASV, cancellationToken);
            if (pasvResult.FtpStatusCode != FtpStatusCode.EnteringPassive)
                throw new FtpException(pasvResult.ResponseMessage);

            passivePortNumber = pasvResult.ResponseMessage.ExtractPasvPortNumber();
        }

        if (!passivePortNumber.HasValue)
            throw new FtpException("Could not determine EPSV/PASV data port");

        return await ControlStream.OpenDataStreamAsync(Configuration.Host, passivePortNumber.Value, cancellationToken);
    }

    /// <summary>
    /// Throws an exception if the server response is not one of the given acceptable codes
    /// </summary>
    /// <param name="response"></param>
    /// <param name="codes"></param>
    /// <returns></returns>
    private async Task BailIfResponseNotAsync(FtpResponse response, CancellationToken cancellationToken, params FtpStatusCode[] codes)
    {
        if (codes.Any(x => x == response.FtpStatusCode))
            return;

        Logger?.LogDebug("Bailing due to response codes being {ftpStatusCode}, which is not one of: [{codes}]",
            response.FtpStatusCode, string.Join(",", codes));

        await LogOutAsync(cancellationToken);
        throw new FtpException(response.ResponseMessage);
    }

    /// <summary>
    /// Determine if the FTP server supports UTF8 encoding, and set it to the default if possible
    /// </summary>
    /// <returns></returns>
    private async Task EnableUTF8IfPossible()
    {
        if (Equals(ControlStream.Encoding, Encoding.ASCII) && Features.Any(x => x == Constants.UTF8))
        {
            ControlStream.Encoding = Encoding.UTF8;
        }

        if (Equals(ControlStream.Encoding, Encoding.UTF8))
        {
            // If the server supports UTF8 it should already be enabled and this
            // command should not matter however there are conflicting drafts
            // about this so we'll just execute it to be safe.
            await ControlStream.SendCommandAsync("OPTS UTF8 ON");
        }
    }
}
