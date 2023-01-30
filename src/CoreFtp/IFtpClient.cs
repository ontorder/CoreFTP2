using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CoreFtp
{
    public interface IFtpClient : IDisposable
    {
        bool IsAuthenticated { get; }

        bool IsConnected { get;  }

        bool IsEncrypted { get; }

        ILogger Logger { set; }

        string WorkingDirectory { get; }

        Task ChangeWorkingDirectoryAsync(string directory);

        Task CloseFileDataStreamAsync(CancellationToken ctsToken = default);

        void Configure(FtpClientConfiguration configuration);

        Task CreateDirectoryAsync(string directory);

        Task DeleteDirectoryAsync(string directory);

        Task DeleteFileAsync(string fileName);

        Task<long> GetFileSizeAsync(string fileName);

        Task LoginAsync();

        Task LogOutAsync();

        Task<Stream> OpenFileReadStreamAsync(string fileName);

        Task<Stream> OpenFileWriteStreamAsync(string fileName);

        Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync();

        Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync();

        Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy);

        Task RenameAsync(string from, string to);

        Task SetTransferMode(FtpTransferMode transferMode, char secondType = '\0');

        Task<FtpResponse> SendCommandAsync(FtpCommandEnvelope envelope, CancellationToken token = default(CancellationToken));

        Task<FtpResponse> SendCommandAsync(string command, CancellationToken token = default(CancellationToken));

        Task<FtpResponse> SetClientName(string clientName);
    }
}
