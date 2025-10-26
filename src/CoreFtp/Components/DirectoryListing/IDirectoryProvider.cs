using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace CoreFtp.Components.DirectoryListing;

internal interface IDirectoryProvider
{
    /// <summary>
    /// Lists all nodes in the current working directory
    /// </summary>
    /// <returns></returns>
    Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all files in the current working directory
    /// </summary>
    /// <returns></returns>
    Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(Enum.DirSort? sortBy = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists directories beneath the current working directory
    /// </summary>
    /// <returns></returns>
    Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default);
}
