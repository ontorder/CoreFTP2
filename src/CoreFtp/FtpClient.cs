using CoreFtp.Components.DirectoryListing;
using CoreFtp.Components.DnsResolution;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using CoreFtp.Infrastructure.Stream;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CoreFtp;

public sealed class FtpClient : IFtpClient
{
    public FtpClientConfiguration Configuration { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public bool IsConnected => ControlStream != null && ControlStream.IsConnected;
    public bool IsEncrypted => ControlStream != null && ControlStream.IsEncrypted;
    public ILogger? Logger { private get => _logger; set => (_logger, ControlStream.Logger) = (value, value); }
    public string WorkingDirectory { get; private set; } = "/";

    internal FtpControlStream ControlStream { get; private set; }
    internal readonly SemaphoreSlim DataSocketSemaphore = new(1, 1);
    internal ICollection<string> Features { get; private set; } = Array.Empty<string>();

    private enum DirectoryProviderType { mlsd, list, uninitialized }

    private DirectoryProviderType _directoryProviderType = DirectoryProviderType.uninitialized;
    private ILogger? _logger;

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
        _logger?.LogTrace("[CoreFtp] changing directory to {directory}", directory);
        if (directory.IsNullOrWhiteSpace() || directory.Equals("."))
            throw new ArgumentOutOfRangeException(nameof(directory), "Supplied directory was incorrect");

        EnsureLoggedIn();

        var cwdCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
        var reader = await ControlStream.SendReadAsync(cwdCmd, cancellationToken);
        var cwdResponse = await FtpModelParser.ParseCwdAsync(reader);

        //if (cwdResponse.FtpStatusCode != FtpStatusCode.FileActionOK)
        //    _logger?.LogWarning("[CoreFtp] cwd response was not 250: '{code} {msg}'", (int)cwdResponse.FtpStatusCode, cwdResponse.ResponseMessage);

        if (cwdResponse == false)
            throw new FtpException("cwd fail");

        reader = await ControlStream.SendCommandReaderAsync(FtpCommand.PWD, cancellationToken);
        var pwdResponse = await FtpModelParser.ParsePwdAsync(reader);

        //if (pwdResponse.FtpStatusCode != FtpStatusCode.PathnameCreated)
        //    _logger?.LogWarning("[CoreFtp] pwd response was not 257: '{code} {msg}'", (int)pwdResponse.FtpStatusCode, pwdResponse.ResponseMessage);

        if (pwdResponse.Ok == false)
            throw new FtpException("pwd fail");

        //const char TrimChar = '"';
        //if (pwdResponse.ResponseMessage.Contains(TrimChar) == false)
        //{
        //    _logger?.LogWarning("[CoreFtp] pwd failed? '{code} {resp}'\ncwd: '{code} {cwd}'",
        //        (int)pwdResponse.FtpStatusCode, pwdResponse.ResponseMessage, (int)cwdResponse.FtpStatusCode, cwdResponse.ResponseMessage);
        //    throw new Exception($"pwd response '{pwdResponse.ResponseMessage}' has no '{TrimChar}'\n'");
        //}
        //var splitted = pwdResponse.ResponseMessage.Split(TrimChar);
        WorkingDirectory = pwdResponse.Path;
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

        _logger?.LogDebug("[CoreFtp] Creating directory {directory}", directory);
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

        _logger?.LogDebug("[CoreFtp] Deleting directory {directory}", directory);

        EnsureLoggedIn();

        var rmdCmd = new FtpCommandEnvelope(FtpCommand.RMD, directory);
        var rmdResponse = await ControlStream.SendCommandReadFtpResponseAsync(rmdCmd, cancellationToken);

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
        _logger?.LogDebug("[CoreFtp] Deleting file {fileName}", fileName);
        var deleCmd = new FtpCommandEnvelope(FtpCommand.DELE, fileName);
        var response = await ControlStream.SendCommandReadFtpResponseAsync(deleCmd, cancellationToken);

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
        _logger?.LogDebug("[CoreFtp] Getting file size for {fileName}", fileName);
        var sizeCmd = new FtpCommandEnvelope(FtpCommand.SIZE, fileName);
        var sizeResponse = await ControlStream.SendCommandReadFtpResponseAsync(sizeCmd, cancellationToken);

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
            _logger?.LogDebug("[CoreFtp] Listing files in {WorkingDirectory}", WorkingDirectory);
            await DataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            return await dp.ListAllAsync(cancellationToken);
        }
        finally
        {
            DataSocketSemaphore.Release();
            _ = await ControlStream.GetFtpResponseAsync(cancellationToken);
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
            _logger?.LogDebug("[CoreFtp] Listing files in {WorkingDirectory}", WorkingDirectory);
            await DataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            return await dp.ListFilesAsync(sortBy, cancellationToken);
        }
        finally
        {
            DataSocketSemaphore.Release();
            _ = await ControlStream.GetFtpResponseAsync(cancellationToken);
        }
    }

    public async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        try
        {
            _logger?.LogDebug("[CoreFtp] Listing files in {WorkingDirectory}", WorkingDirectory);
            await DataSocketSemaphore.WaitAsync(cancellationToken);

            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            var start = await StartDirectoryEnumAsync(sortBy, cancellationToken);
            if (start == false)
            {
                // log problema
                yield break;
            }

            await foreach (var file in dp.ListFilesAsyncEnum(sortBy, cancellationToken))
                yield return file;
        }
        finally
        {
            DataSocketSemaphore.Release();
            try
            {
                _ = await ControlStream.GetFtpResponseAsync(cancellationToken);
            }
            catch (Exception respErr)
            {
                _logger?.LogError(respErr, "[CoreFtp] exception in list files");
            }
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
            _logger?.LogDebug("[CoreFtp] Listing directories in {WorkingDirectory}", WorkingDirectory);
            await DataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            return await dp.ListDirectoriesAsync(cancellationToken);
        }
        finally
        {
            DataSocketSemaphore.Release();
            _ = await ControlStream.GetFtpResponseAsync(cancellationToken);
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
        var reader = await ControlStream.SendReadAsync(userCmd, cancellationToken);
        var userResponse = await FtpModelParser.ParseUserAsync(reader);
        //await BailIfResponseNotAsync(userResponse, cancellationToken, FtpStatusCode.SendUserCommand, FtpStatusCode.SendPasswordCommand, FtpStatusCode.LoggedInProceed);
        //if (userResponse.FtpStatusCode != FtpStatusCode.SendPasswordCommand)
        //    _logger?.LogWarning("[CoreFtp] user response was not 331: '{code} {msg}'", (int)userResponse.FtpStatusCode, userResponse.ResponseMessage);
        if (userResponse == false)
            _logger?.LogWarning("[CoreFtp] user response false");

        var passCmd = new FtpCommandEnvelope(FtpCommand.PASS, username != Constants.ANONYMOUS_USER ? Configuration.Password : string.Empty);
        reader = await ControlStream.SendReadAsync(passCmd, cancellationToken);
        var passResponse = await FtpModelParser.ParsePassAsync(reader);
        //await BailIfResponseNotAsync(passResponse, cancellationToken, FtpStatusCode.LoggedInProceed);
        //if (passResponse.FtpStatusCode != FtpStatusCode.LoggedInProceed)
        //    _logger?.LogWarning("[CoreFtp] pass response was not 230: '{code} {msg}'", (int)passResponse.FtpStatusCode, passResponse.ResponseMessage);
        if (passResponse == false)
            _logger?.LogWarning("[CoreFtp] pass response false");

        IsAuthenticated = true;

        if (ControlStream.IsEncrypted)
        {
            var pbszCmd = new FtpCommandEnvelope(FtpCommand.PBSZ, "0");
            await ControlStream.SendCommandReadFtpResponseAsync(pbszCmd, cancellationToken);

            var protCmd = new FtpCommandEnvelope(FtpCommand.PROT, "P");
            await ControlStream.SendCommandReadFtpResponseAsync(protCmd, cancellationToken);
        }

        Features = await DetermineFeaturesAsync(cancellationToken);
        _directoryProviderType = DetermineDirectoryProvider();
        await EnableUtf8IfPossible();
        await SetTransferMode(Configuration.Mode, Configuration.ModeSecondType);

        if (Configuration.BaseDirectory != "/")
        {
            await CreateDirectoryAsync(Configuration.BaseDirectory, cancellationToken);
        }

        await ChangeWorkingDirectoryAsync(Configuration.BaseDirectory, cancellationToken);
    }

    /// <summary>
    /// Attemps to log the user out asynchronously, sends the QUIT command and terminates the command socket.
    /// </summary>
    public async Task LogOutAsync(CancellationToken cancellationToken = default)
    {
        await IgnoreStaleData(cancellationToken);
        if (IsConnected == false)
            return;

        _logger?.LogTrace("[CoreFtp] Logging out");
        await ControlStream.SendCommandReaderAsync(FtpCommand.QUIT, cancellationToken);
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
        _logger?.LogDebug("[CoreFtp] Opening file read stream for {fileName}", fileName);
        return await OpenFileStreamAsync(fileName, FtpCommand.RETR, cancellationToken);
    }

    /// <summary>
    /// Provides a stream which can be written to
    /// </summary>
    /// <param name="fileName"></param>
    /// <returns></returns>
    public async Task<Stream> OpenFileWriteStreamAsync(string fileName, CancellationToken cancellationToken)
    {
        string filePath = WorkingDirectory.CombineAsUriWith(fileName);
        _logger?.LogDebug("[CoreFtp] Opening file read stream for {filePath}", filePath);
        var segments = filePath
            .Split('/')
            .Where(x => !x.IsNullOrWhiteSpace())
            .ToList();
        await CreateDirectoryStructureRecursively(segments.Take(segments.Count - 1).ToArray(), filePath.StartsWith("/"), cancellationToken);
        return await OpenFileStreamAsync(filePath, FtpCommand.STOR, cancellationToken);
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
        _logger?.LogDebug("[CoreFtp] Renaming from {from}, to {to}", from, to);

        var rnfrCmd = new FtpCommandEnvelope(FtpCommand.RNFR, from);
        var renameFromResponse = await ControlStream.SendCommandReadFtpResponseAsync(rnfrCmd, cancellationToken);

        if (renameFromResponse.FtpStatusCode != FtpStatusCode.FileCommandPending)
            throw new FtpException(renameFromResponse.ResponseMessage);

        var rntoCmd = new FtpCommandEnvelope(FtpCommand.RNTO, to);
        var renameToResponse = await ControlStream.SendCommandReadFtpResponseAsync(rntoCmd, cancellationToken);

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
        _logger?.LogDebug("[CoreFtp] Setting client name to {clientName}", clientName);

        var clntCmd = new FtpCommandEnvelope(FtpCommand.CLNT, clientName);
        return await ControlStream.SendCommandReadFtpResponseAsync(clntCmd, cancellationToken);
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
        _logger?.LogTrace("[CoreFtp] Setting transfer mode {transferMode}, {secondType}", transferMode, secondType);
        var typeCmd = new FtpCommandEnvelope(
            FtpCommand.TYPE,
            secondType != '\0'
                ? $"{(char)transferMode} {secondType}"
                : $"{(char)transferMode}"
        );
        var reader = await ControlStream.SendReadAsync(typeCmd);
        var response = await FtpModelParser.ParseTypeAsync(reader);

        //if (response.FtpStatusCode != FtpStatusCode.CommandOK)
        //    _logger?.LogWarning("[CoreFtp] type response was not 200: '{code} {msg}'", (int)response.FtpStatusCode, response.ResponseMessage);

        //if (response.IsSuccess == false)
        //    throw new FtpException(response.ResponseMessage);

        if (response == false)
            throw new FtpException("type returned false");
    }

    public async Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default)
        => await ControlStream.SendCommandReadFtpResponseAsync(envelope, token);

    public async Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default)
        => await ControlStream.SendRead2Async(command, token);

    /// <summary>
    /// Ignore any stale data we may have waiting on the stream
    /// </summary>
    /// <returns></returns>
    public void Dispose()
    {
        _logger?.LogDebug("[CoreFtp] Disposing of FtpClient");
        Task.WaitAny(LogOutAsync(default));
        ControlStream.Dispose();
        DataSocketSemaphore.Dispose();
    }

    private async Task IgnoreStaleData(CancellationToken cancellationToken)
    {
        if (IsConnected && ControlStream.SocketDataAvailable() is int dataSize && dataSize > 0)
        {
            var staleData = await ControlStream.GetFtpResponseAsync(cancellationToken);
            _logger?.LogWarning("[CoreFtp] Stale data detected ({size}): {msg}", dataSize, staleData.ResponseMessage);
        }
    }

    /// <summary>
    /// Determines the type of directory listing the FTP server will return, and set the appropriate parser
    /// </summary>
    /// <returns></returns>
    private DirectoryProviderType DetermineDirectoryProvider()
    {
        _logger?.LogTrace("[CoreFtp] Determining directory provider");
        if (this.UsesMlsd())
            return DirectoryProviderType.mlsd;

        return DirectoryProviderType.list;
    }

    private async Task<ICollection<string>> DetermineFeaturesAsync(CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        _logger?.LogTrace("[CoreFtp] Determining features");
        var reader = await ControlStream.SendCommandReaderAsync(FtpCommand.FEAT, cancellationToken);
        var response = await FtpModelParser.ParseFeatsAsync(reader);

        //if (response.FtpStatusCode != FtpStatusCode.EndFeats)
        //    _logger?.LogWarning("[CoreFtp] feat response was not 211: '{code} {msg}'", (int)response.FtpStatusCode, response.ResponseMessage);

        //if (response.FtpStatusCode == FtpStatusCode.CommandSyntaxError || response.FtpStatusCode == FtpStatusCode.CommandNotImplemented)
        //    return Enumerable.Empty<string>();

        var features = response.Feats
            .Where(x => !x.StartsWith(((int)FtpStatusCode.SystemHelpReply).ToString()) && !x.IsNullOrWhiteSpace())
            .Select(x => x.Trim())
            .ToArray();

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
        _logger?.LogDebug("[CoreFtp] Creating directory structure recursively {dirs}", string.Join("/", directories));
        string originalPath = WorkingDirectory;

        if (isRootedPath && directories.Any())
            await ChangeWorkingDirectoryAsync("/", cancellationToken);

        if (!directories.Any())
            return;

        if (directories.Count == 1)
        {
            var mkdCmd = new FtpCommandEnvelope(FtpCommand.MKD, directories.First());
            await ControlStream.SendCommandReadFtpResponseAsync(mkdCmd, cancellationToken);

            await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
            return;
        }

        foreach (string directory in directories)
        {
            if (directory.IsNullOrWhiteSpace())
                continue;

            var cmwCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
            var response = await ControlStream.SendCommandReadFtpResponseAsync(cmwCmd, cancellationToken);

            if (response.FtpStatusCode != FtpStatusCode.ActionNotTakenFileUnavailable)
                continue;

            var mkdCmd = new FtpCommandEnvelope(FtpCommand.MKD, directory);
            await ControlStream.SendCommandReadFtpResponseAsync(mkdCmd, cancellationToken);
            var cwdCmd = new FtpCommandEnvelope(FtpCommand.CWD, directory);
            await ControlStream.SendCommandReadFtpResponseAsync(cwdCmd, cancellationToken);
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
        _logger?.LogDebug("[CoreFtp] Opening filestream for {fileName}, {command}", fileName, command);
        var dataStream = await ConnectDataStreamAsync(cancellationToken);

        var ftpCmd = new FtpCommandEnvelope(command, fileName);
        var retrResponse = await ControlStream.SendCommandReadFtpResponseAsync(ftpCmd, cancellationToken);

        if ((retrResponse.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) &&
             (retrResponse.FtpStatusCode != FtpStatusCode.OpeningData) &&
             (retrResponse.FtpStatusCode != FtpStatusCode.ClosingData))
            throw new FtpException(retrResponse.ResponseMessage);

        return dataStream;
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
    private async Task<Stream> ConnectDataStreamAsync(CancellationToken cancellationToken)
    {
        _logger?.LogTrace("[CoreFtp] Connecting to a data socket");

        var reader = await ControlStream.SendCommandReaderAsync(FtpCommand.EPSV, cancellationToken);
        var epsvResult = await FtpModelParser.ParseEpsvAsync(reader);

        int? passivePortNumber;
        if (epsvResult.Ok)
        {
            passivePortNumber = epsvResult.Port;
        }
        else
        {
            // EPSV failed - try regular PASV
            reader = await ControlStream.SendCommandReaderAsync(FtpCommand.PASV, cancellationToken);
            var pasvResult = await FtpModelParser.ParsePasvAsync(reader);
            if (pasvResult.Ok == false)
                throw new FtpException("pasv fail");

            passivePortNumber = pasvResult.Port;
        }

        if (passivePortNumber.HasValue == false)
            throw new FtpException("Could not determine EPSV/PASV data port");

        return await ControlStream.OpenDataStreamAsync(Configuration.Host, passivePortNumber.Value, cancellationToken)
            ?? throw new FtpException("Could not establish a data connection");
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

        _logger?.LogDebug("[CoreFtp] Bailing due to response codes being {ftpStatusCode}, which is not one of: [{codes}]",
            response.FtpStatusCode, string.Join(",", codes));

        await LogOutAsync(cancellationToken);
        throw new FtpException(response.ResponseMessage);
    }

    /// <summary>
    /// Determine if the FTP server supports UTF8 encoding, and set it to the default if possible
    /// </summary>
    /// <returns></returns>
    private async Task EnableUtf8IfPossible()
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
            var reader = await ControlStream.SendCommandReaderAsync("OPTS UTF8 ON");
            _ = await FtpModelParser.ParseOptsAsync(reader);
        }
    }

    private IDirectoryProvider InitDirectoryProvider(Stream stream)
        => _directoryProviderType switch
        {
            DirectoryProviderType.mlsd => new MlsdDirectoryProvider(ControlStream.Encoding, _logger, stream),
            DirectoryProviderType.list => new ListDirectoryProvider(ControlStream.Encoding, _logger, stream),
            _ => throw new InvalidOperationException("Directory provider type not initialized"),
        };

    private async Task<bool> StartDirectoryEnumAsync(DirSort? sortBy, CancellationToken cancellation)
    {
        switch (_directoryProviderType)
        {
            case DirectoryProviderType.mlsd:
            {
                var reader = await ControlStream.SendReadAsync(new FtpCommandEnvelope(FtpCommand.MLSD), cancellation);
                return await FtpModelParser.ParseMlsdAsync(reader);
            }

            case DirectoryProviderType.list:
                string arguments = sortBy switch
                {
                    DirSort.Alphabetical => "-1",
                    DirSort.AlphabeticalReverse => "-r",
                    DirSort.ModifiedTimestampReverse => "-t",
                    _ => String.Empty,
                };
                var listCmd = new FtpCommandEnvelope(FtpCommand.LIST, arguments);
                var listResult = await ControlStream.SendCommandReadFtpResponseAsync(listCmd, cancellation);
                return listResult.IsSuccess;

            default:
                throw new InvalidOperationException("Directory provider type not initialized");
        }
    }
}
