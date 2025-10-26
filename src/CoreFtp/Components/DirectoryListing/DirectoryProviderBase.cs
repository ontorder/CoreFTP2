using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace CoreFtp.Components.DirectoryListing;

public abstract class DirectoryProviderBase : IDirectoryProvider
{
    protected Infrastructure.Stream.FtpTextDataStream DataStream;
    protected ILogger? Logger;
    protected Encoding MyEncoding;

    protected DirectoryProviderBase(ILogger? logger, Encoding myEncoding, Infrastructure.Stream.FtpTextDataStream stream)
    {
        Logger = logger;
        MyEncoding = myEncoding;
        DataStream = stream;
    }

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public virtual IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
