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

    public FtpClient(FtpClientConfiguration configuration)
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
        _logger?.LogTrace("[FtpClient] changing directory to {directory}", directory);
        if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
            throw new ArgumentOutOfRangeException(nameof(directory), "Supplied directory was incorrect");

        EnsureLoggedIn();

        var cwdCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
        var cwdResponse = await ControlStream.SendCommandAsync(cwdCmd, cancellationToken);

        if (cwdResponse.IsSuccess == false)
            throw new FtpException(cwdResponse.ResponseMessage);

        var pwdResponse = await ControlStream.SendCommandAsync(FtpCommand.PWD, cancellationToken);

        if (pwdResponse.IsSuccess == false)
            throw new FtpException(pwdResponse.ResponseMessage);

        const char TrimChar = '"';

        if (pwdResponse.ResponseMessage.Contains(TrimChar) == false)
        {
            _logger?.LogWarning("[FtpClient] pwd failed? '{resp}'\ncwd: '{cwd}'", pwdResponse.ResponseMessage, cwdResponse.ResponseMessage);
            throw new Exception($"pwd response '{pwdResponse.ResponseMessage}' has no '{TrimChar}'\n'");
        }
        var splitted = pwdResponse.ResponseMessage.Split(TrimChar);
        WorkingDirectory = splitted[1];
    }

    /// <summary>
    /// Closes the write stream and associated socket (if open),
    /// </summary>
    /// <param name="ctsToken"></param>
    /// <returns></returns>
    public async Task CloseFileDataStreamAsync(CancellationToken ctsToken = default)
    {
        _logger?.LogTrace("[FtpClient] Closing write file stream");
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

        _logger?.LogDebug("[FtpClient] Creating directory {directory}", directory);
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

        _logger?.LogDebug("[FtpClient] Deleting directory {directory}", directory);

        EnsureLoggedIn();

        var rmdCmd = new FtpCommandEnvelope(FtpCommand.RMD, directory);
        var rmdResponse = await ControlStream.SendCommandAsync(rmdCmd, cancellationToken);

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
        _logger?.LogDebug("[FtpClient] Deleting file {fileName}", fileName);
        var deleCmd = new FtpCommandEnvelope(FtpCommand.DELE, fileName);
        var response = await ControlStream.SendCommandAsync(deleCmd, cancellationToken);

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
        _logger?.LogDebug("[FtpClient] Getting file size for {fileName}", fileName);
        var sizeCmd = new FtpCommandEnvelope(FtpCommand.SIZE, fileName);
        var sizeResponse = await ControlStream.SendCommandAsync(sizeCmd, cancellationToken);

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
            _logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
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
            _logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
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
            _logger?.LogDebug("[FtpClient] Listing files in {WorkingDirectory}", WorkingDirectory);
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
            _logger?.LogDebug("[FtpClient] Listing directories in {WorkingDirectory}", WorkingDirectory);
            return await _directoryProvider.ListDirectoriesAsync(cancellationToken);
        }
        finally
        {
            await ControlStream.GetResponseAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Attempts to log the user in to the FTP Server
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

        var userCmd = new FtpCommandEnvelope(FtpCommand.USER, username);
        var userResponse = await ControlStream.SendCommandAsync(userCmd, cancellationToken);

        await BailIfResponseNotAsync(userResponse, cancellationToken, FtpStatusCode.SendUserCommand, FtpStatusCode.SendPasswordCommand, FtpStatusCode.LoggedInProceed);

        FtpCommandEnvelope oassCmd = new FtpCommandEnvelope(FtpCommand.PASS, username != Constants.ANONYMOUS_USER ? Configuration.Password : string.Empty);
        var passResponse = await ControlStream.SendCommandAsync(oassCmd, cancellationToken);

        await BailIfResponseNotAsync(passResponse, cancellationToken, FtpStatusCode.LoggedInProceed);
        IsAuthenticated = true;

        if (ControlStream.IsEncrypted)
        {
            var pbszCmd = new FtpCommandEnvelope(FtpCommand.PBSZ, "0");
            await ControlStream.SendCommandAsync(pbszCmd, cancellationToken);

            var protCmd = new FtpCommandEnvelope(FtpCommand.PROT, "P");
            await ControlStream.SendCommandAsync(protCmd, cancellationToken);
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

        _logger?.LogTrace("[FtpClient] Logging out");
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
        _logger?.LogDebug("[FtpClient] Opening file read stream for {fileName}", fileName);
        return new FtpDataStream(await OpenFileStreamAsync(fileName, FtpCommand.RETR, cancellationToken), this, _logger);
    }

    /// <summary>
    /// Provides a stream which can be written to
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task<Stream> OpenFileWriteStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        string filePath = WorkingDirectory.CombineAsUriWith(fileName);
        _logger?.LogDebug("[FtpClient] Opening file read stream for {filePath}", filePath);
        var segments = filePath
            .Split('/')
            .Where(x => !x.IsNullOrWhiteSpace())
            .ToList();
        await CreateDirectoryStructureRecursively(segments.Take(segments.Count - 1).ToArray(), filePath.StartsWith("/"), cancellationToken);
        return new FtpDataStream(await OpenFileStreamAsync(filePath, FtpCommand.STOR, cancellationToken), this, _logger);
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
        _logger?.LogDebug("[FtpClient] Renaming from {from}, to {to}", from, to);

        var rnfrCmd = new FtpCommandEnvelope(FtpCommand.RNFR, from);
        var renameFromResponse = await ControlStream.SendCommandAsync(rnfrCmd, cancellationToken);

        if (renameFromResponse.FtpStatusCode != FtpStatusCode.FileCommandPending)
            throw new FtpException(renameFromResponse.ResponseMessage);

        var rntoCmd = new FtpCommandEnvelope(FtpCommand.RNTO, to);
        var renameToResponse = await ControlStream.SendCommandAsync(rntoCmd, cancellationToken);

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
        _logger?.LogDebug("[FtpClient] Setting client name to {clientName}", clientName);

        var clntCmd = new FtpCommandEnvelope(FtpCommand.CLNT, clientName);
        return await ControlStream.SendCommandAsync(clntCmd, cancellationToken);
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
        _logger?.LogTrace("[FtpClient] Setting transfer mode {transferMode}, {secondType}", transferMode, secondType);
        var typeCmd = new FtpCommandEnvelope(
            FtpCommand.TYPE,
            secondType != '\0'
                ? $"{(char)transferMode} {secondType}"
                : $"{(char)transferMode}"
        );
        var response = await ControlStream.SendCommandAsync(typeCmd);

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
        _logger?.LogDebug("Disposing of FtpClient");
        Task.WaitAny(LogOutAsync(default));
        ControlStream?.Dispose();
        DataSocketSemaphore?.Dispose();
    }

    private async Task IgnoreStaleData(CancellationToken cancellationToken)
    {
        if (IsConnected && ControlStream.SocketDataAvailable())
        {
            var staleData = await ControlStream.GetResponseAsync(cancellationToken);
            _logger?.LogWarning("Stale data detected: {msg}", staleData.ResponseMessage);
        }
    }

    /// <summary>
    /// Determines the type of directory listing the FTP server will return, and set the appropriate parser
    /// </summary>
    /// <returns></returns>
    private IDirectoryProvider DetermineDirectoryProvider()
    {
        _logger?.LogTrace("[FtpClient] Determining directory provider");
        if (this.UsesMlsd())
            return new MlsdDirectoryProvider(this, _logger, Configuration);

        return new ListDirectoryProvider(this, _logger, Configuration);
    }

    private async Task<IEnumerable<string>> DetermineFeaturesAsync(CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        _logger?.LogTrace("[FtpClient] Determining features");
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
        _logger?.LogDebug("[FtpClient] Creating directory structure recursively {dirs}", string.Join("/", directories));
        string originalPath = WorkingDirectory;

        if (isRootedPath && directories.Any())
            await ChangeWorkingDirectoryAsync("/", cancellationToken);

        if (!directories.Any())
            return;

        if (directories.Count == 1)
        {
            var mkdCmd = new FtpCommandEnvelope(FtpCommand.MKD, directories.First());
            await ControlStream.SendCommandAsync(mkdCmd, cancellationToken);

            await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
            return;
        }

        foreach (string directory in directories)
        {
            if (directory.IsNullOrWhiteSpace())
                continue;

            var cmwCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
            var response = await ControlStream.SendCommandAsync(cmwCmd, cancellationToken);

            if (response.FtpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                continue;

            var mkdCmd = new FtpCommandEnvelope(FtpCommand.MKD, directory);
            await ControlStream.SendCommandAsync(mkdCmd, cancellationToken);
            var cwdCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
            await ControlStream.SendCommandAsync(cwdCmd, cancellationToken);
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
        _logger?.LogDebug("[FtpClient] Opening filestream for {fileName}, {command}", fileName, command);
        _dataStream = await ConnectDataStreamAsync(cancellationToken);

        var ftpCmd = new FtpCommandEnvelope(command, fileName);
        var retrResponse = await ControlStream.SendCommandAsync(ftpCmd, cancellationToken);

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
        _logger?.LogTrace("[FtpClient] Connecting to a data socket");

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

        _logger?.LogDebug("Bailing due to response codes being {ftpStatusCode}, which is not one of: [{codes}]",
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
