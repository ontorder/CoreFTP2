using CoreFtp.Components.DirectoryListing;
using CoreFtp.Components.DnsResolution;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using CoreFtp.Infrastructure.Stream;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace CoreFtp;

public sealed class FtpClient : IFtpClient
{
    public FtpClientConfiguration Configuration { get; private set; }
    internal ICollection<string> Features => _features;
    public bool IsAuthenticated { get; private set; }
    public bool IsConnected => _controlStream != null && _controlStream.IsConnected;
    public bool IsEncrypted => _controlStream != null && _controlStream.IsEncrypted;
    public string WorkingDirectory { get; private set; } = "/";

    private readonly FtpControlStream _controlStream;
    private readonly SemaphoreSlim _dataSocketSemaphore = new(1, 1);
    private DirectoryProviderType _directoryProviderType = DirectoryProviderType.Uninitialized;
    private ICollection<string> _features = Array.Empty<string>();
    private readonly ILogger? _logger;

    public FtpClient(FtpClientConfiguration configuration, ILogger logger)
    {
        Configuration = configuration;
        _logger = logger;

        if (configuration.Host == null)
            throw new ArgumentNullException(nameof(configuration.Host));

        if (Uri.IsWellFormedUriString(configuration.Host, UriKind.Absolute))
        {
            configuration.Host = new Uri(configuration.Host).Host;
        }

        _controlStream = new FtpControlStream(Configuration, new DnsResolver(), _logger);
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

        var resp = await _controlStream.CwdAsync(directory, cancellationToken);
        if (resp == false)
            throw new FtpException("cwd fail");

        var pwd = await _controlStream.PwdAsync(cancellationToken);
        if (pwd == null)
            _logger?.LogWarning("[CoreFtp] pwd response was not 257");

        WorkingDirectory = pwd;
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

        var resp = await _controlStream.RmdAsync(directory, cancellationToken);
        if (resp == RmdResult.Error) throw new Exception("RMD failed");
        if (resp == RmdResult.NotEmpty) await DeleteNonEmptyDirectory(directory, cancellationToken);
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
        var response = await _controlStream.DeleteAsync(fileName, cancellationToken);
        if (false == response) throw new FtpException("delete file failed");
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

        foreach (var file in allNodes.Where(static x => x.NodeType == FtpNodeType.File))
        {
            await DeleteFileAsync(file.Name, cancellationToken);
        }

        foreach (var dir in allNodes.Where(static x => x.NodeType == FtpNodeType.Directory))
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
        return await _controlStream.SizeAsync(fileName, cancellationToken);
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
            await _dataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            var start = await StartDirectoryEnumAsync(sortBy: null, cancellationToken);
            if (start.Ok == false)
            {
                // log problema
                throw new Exception();
            }
            var endTask = WaitDataEndAsync(dataStream, start.WaitEndAsync!);
            var enumerated = await dp.ListAllAsync(cancellationToken);
            await endTask;
            return enumerated;
        }
        finally
        {
            _dataSocketSemaphore.Release();
        }

        static async Task WaitDataEndAsync(FtpTextDataStream dataStream, Task endDataStreamAsync)
        {
            await endDataStreamAsync;
            dataStream.Close();
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
            await _dataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            var start = await StartDirectoryEnumAsync(sortBy, cancellationToken);
            if (start.Ok == false)
            {
                // log problema
                throw new Exception();
            }
            var endTask = WaitDataEndAsync(dataStream, start.WaitEndAsync!);
            var enumerated = await dp.ListFilesAsync(sortBy, cancellationToken);
            await endTask;
            return enumerated;
        }
        finally
        {
            _dataSocketSemaphore.Release();
        }

        static async Task WaitDataEndAsync(FtpTextDataStream dataStream, Task endDataStreamAsync)
        {
            await endDataStreamAsync;
            dataStream.Close();
        }
    }

    public async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(
        DirSort? sortBy = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        try
        {
            _logger?.LogDebug("[CoreFtp] Listing files in {WorkingDirectory}", WorkingDirectory);
            await _dataSocketSemaphore.WaitAsync(cancellationToken);

            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            var start = await StartDirectoryEnumAsync(sortBy, cancellationToken);
            if (start.Ok == false)
            {
                // log problema
                yield break;
            }
            var endTask = WaitDataEndAsync(dataStream, start.WaitEndAsync!);
            await foreach (var file in dp.ListFilesAsyncEnum(sortBy, cancellationToken))
                yield return file;
            await endTask;
        }
        finally
        {
            _dataSocketSemaphore.Release();
        }

        static async Task WaitDataEndAsync(FtpTextDataStream dataStream, Task endDataStreamAsync)
        {
            await endDataStreamAsync;
            dataStream.Close();
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
            await _dataSocketSemaphore.WaitAsync(cancellationToken);
            var dataStream = await ConnectDataStreamAsync(cancellationToken);
            var dp = InitDirectoryProvider(dataStream);
            return await dp.ListDirectoriesAsync(cancellationToken);
        }
        finally
        {
            _dataSocketSemaphore.Release();
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

        await _controlStream.ConnectAsync(cancellationToken);

        var userResponse = await _controlStream.SendUserAsync(username, cancellationToken);
        if (userResponse == false)
            _logger?.LogWarning("[CoreFtp] user response false");

        var passCmd = username != Constants.ANONYMOUS_USER ? Configuration.Password : string.Empty;
        var passResponse = await _controlStream.SendPassAsync(passCmd, cancellationToken);
        if (passResponse == false)
            _logger?.LogWarning("[CoreFtp] pass response false");

        IsAuthenticated = true;

        if (_controlStream.IsEncrypted)
        {
            await _controlStream.PbszAsync(cancellationToken);
            await _controlStream.ProtAsync(cancellationToken);
        }

        _features = await DetermineFeaturesAsync(cancellationToken);
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
        if (IsConnected == false)
            return;

        _logger?.LogTrace("[CoreFtp] Logging out");
        await _controlStream.QuitAsync(cancellationToken);
        _controlStream.Disconnect();
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
        await _controlStream.RetrAsync(fileName, cancellationToken);
        _logger?.LogDebug("[CoreFtp] Opening filestream for storing {fileName}", fileName);
        var dataStream = await ConnectDataStreamAsync(cancellationToken);
        return dataStream.GetStream();
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
            .Where(static x => !x.IsNullOrWhiteSpace())
            .ToList();
        await CreateDirectoryStructureRecursively(segments.Take(segments.Count - 1).ToArray(), filePath.StartsWith("/"), cancellationToken);

        await _controlStream.StorAsync(fileName, cancellationToken);
        _logger?.LogDebug("[CoreFtp] Opening filestream for storing {fileName}", fileName);
        var dataStream = await ConnectDataStreamAsync(cancellationToken);
        return dataStream.GetStream();
    }

    /// <summary>
    /// Renames a file on the FTP server
    /// </summary>
    /// <param name="fromName"></param>
    /// <param name="toName"></param>
    /// <returns></returns>
    public async Task RenameAsync(string fromName, string toName, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        _logger?.LogDebug("[CoreFtp] Renaming from {from}, to {to}", fromName, toName);

        await _controlStream.RenameAsync(fromName, toName, cancellationToken);
    }

    /// <summary>
    /// Informs the FTP server of the client being used
    /// </summary>
    /// <param name="clientName"></param>
    /// <returns></returns>
    public async Task SetClientNameAsync(string clientName, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        _logger?.LogDebug("[CoreFtp] Setting client name to {clientName}", clientName);
        await _controlStream.ClntAsync(clientName, cancellationToken);
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
        await _controlStream.TypeAsync(transferMode, secondType);
    }

    public void Dispose()
    {
        _logger?.LogDebug("[CoreFtp] Disposing of FtpClient");
        Task.WaitAny(LogOutAsync(default));
        _controlStream.Dispose(disposing: true);
        _dataSocketSemaphore.Dispose();
    }

    /// <summary>
    /// Determines the type of directory listing the FTP server will return, and set the appropriate parser
    /// </summary>
    /// <returns></returns>
    private DirectoryProviderType DetermineDirectoryProvider()
    {
        _logger?.LogTrace("[CoreFtp] Determining directory provider");
        if (this.UsesMlsd())
            return DirectoryProviderType.MLSD;

        return DirectoryProviderType.LIST;
    }

    private async Task<ICollection<string>> DetermineFeaturesAsync(CancellationToken cancellationToken)
    {
        EnsureLoggedIn();
        _logger?.LogTrace("[CoreFtp] Determining features");
        return await _controlStream.GetFeatsAsync(cancellationToken);
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
            await _controlStream.MkdAsync(directories.First(), cancellationToken);
            await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
            return;
        }

        foreach (string directory in directories)
        {
            if (directory.IsNullOrWhiteSpace())
                continue;

            var response = await _controlStream.CwdAsync(directory, cancellationToken);
            if (response) // if (response != CFtpStatusCode.Code550ActionNotTakenFileUnavailable)
                continue;

            await _controlStream.MkdAsync(directory, cancellationToken);
            await _controlStream.CwdAsync(directory, cancellationToken);
        }

        await ChangeWorkingDirectoryAsync(originalPath, cancellationToken);
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
    private async Task<FtpTextDataStream> ConnectDataStreamAsync(CancellationToken cancellationToken)
    {
        _logger?.LogTrace("[CoreFtp] Connecting to a data socket");

        var portMaybe = await _controlStream.EpsvAsync(cancellationToken);

        int? passivePortNumber;
        if (portMaybe.HasValue)
        {
            passivePortNumber = portMaybe.Value;
        }
        else
        {
            // EPSV failed - try regular PASV
            portMaybe = await _controlStream.PasvAsync(cancellationToken);
            if (false == portMaybe.HasValue)
                throw new FtpException("pasv fail");

            passivePortNumber = portMaybe.Value;
        }

        if (passivePortNumber.HasValue == false)
            throw new FtpException("Could not determine EPSV/PASV data port");

        return await OpenDataStreamAsync(Configuration.Host, passivePortNumber.Value, cancellationToken)
            ?? throw new FtpException("Could not establish a data connection");
    }

    /// <summary>
    /// Determine if the FTP server supports UTF8 encoding, and set it to the default if possible
    /// </summary>
    /// <returns></returns>
    private async Task EnableUtf8IfPossible()
    {
        if (Equals(_controlStream.Encoding, Encoding.ASCII) && _features.Any(static _ => _ == Constants.UTF8))
        {
            _controlStream.Encoding = Encoding.UTF8;
        }

        if (Equals(_controlStream.Encoding, Encoding.UTF8))
        {
            // If the server supports UTF8 it should already be enabled and this
            // command should not matter however there are conflicting drafts
            // about this so we'll just execute it to be safe.
            _ = await _controlStream.SendOptsAsync("UTF8 ON", default);
        }
    }

    private IDirectoryProvider InitDirectoryProvider(FtpTextDataStream stream)
        => _directoryProviderType switch
        {
            DirectoryProviderType.MLSD => new MlsdDirectoryProvider(_controlStream.Encoding, _logger, stream),
            DirectoryProviderType.LIST => new ListDirectoryProvider(_controlStream.Encoding, _logger, stream),
            _ => throw new InvalidOperationException("Directory provider type not initialized"),
        };

    public async Task<FtpTextDataStream> OpenDataStreamAsync(string host, int port, CancellationToken token)
    {
        _logger?.LogDebug("[CoreFtp] FtpSocketStream: Opening datastream");
        var socketStream = new FtpTextDataStream(Configuration, null, _logger); // UNDONE
        await socketStream.TryActivateEncryptionAsync();
        return socketStream;
    }

    private async Task<(bool Ok, Task? WaitEndAsync)> StartDirectoryEnumAsync(DirSort? sortBy, CancellationToken cancellation)
        => _directoryProviderType switch
        {
            DirectoryProviderType.MLSD => await _controlStream.MlsdAsync(cancellation),
            DirectoryProviderType.LIST => await _controlStream.ListAsync(sortBy, cancellation),
            _ => throw new InvalidOperationException("Directory provider type not initialized"),
        };

    private enum DirectoryProviderType { MLSD = 1, LIST = 2, Uninitialized = 0 }
}

file static class FtpClientFeaturesExtensions
{
    public static bool UsesMlsd(this FtpClient client) => client.Features.Any(static _ => _ == "MLSD");

    public static bool UsesEpsv(this FtpClient client) => client.Features.Any(static _ => _ == "EPSV");

    public static bool UsesPasv(this FtpClient client) => client.Features.Any(static _ => _ == "PASV");
}
