using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;

namespace CoreFtp;

public interface IFtpClient : IDisposable
{
    bool IsAuthenticated { get; }
    bool IsConnected { get;  }
    bool IsEncrypted { get; }
    ILogger Logger { set; }
    string WorkingDirectory { get; }

    Task ChangeWorkingDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default);
    Task<long> GetFileSizeAsync(string fileName, CancellationToken cancellationToken = default);
    Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default);
    Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default);
    Task LoginAsync(CancellationToken cancellationToken = default);
    Task LogOutAsync(CancellationToken cancellationToken = default);
    Task<Stream> OpenFileReadStreamAsync(string fileName, CancellationToken cancellationToken = default);
    Task<Stream> OpenFileWriteStreamAsync(string fileName, CancellationToken cancellationToken = default);
    Task RenameAsync(string from, string to, CancellationToken cancellationToken = default);
    Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default);
    Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default);
    Task<FtpResponse> SetClientNameAsync(string clientName, CancellationToken cancellationToken = default);
    Task SetTransferMode(FtpTransferMode transferMode, char secondType = '\0');
}
