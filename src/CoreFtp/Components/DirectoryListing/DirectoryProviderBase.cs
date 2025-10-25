using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreFtp.Components.DirectoryListing;

#nullable enable

public abstract class DirectoryProviderBase : IDirectoryProvider
{
    protected Infrastructure.Stream.FtpControlStream FtpStream;
    protected ILogger? Logger;
    protected Encoding MyEncoding;

    protected DirectoryProviderBase(ILogger? logger, Encoding myEncoding, Infrastructure.Stream.FtpControlStream stream)
    {
        Logger = logger;
        MyEncoding = myEncoding;
        FtpStream = stream;
    }

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public virtual IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    protected async Task<IEnumerable<string>> RetrieveDirectoryListingAsync(CancellationToken cancellationToken)
    {
        var lines = await FtpStream.ReadLinesAsync(MyEncoding, cancellationToken);
        Logger?.LogDebug("[CoreFtp] {lines}", lines);
        return lines;
    }

    protected async IAsyncEnumerable<string> RetrieveDirectoryListingAsyncEnum([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (string line in FtpStream.ReadLineAsyncEnum(MyEncoding, cancellationToken))
        {
            Logger?.LogDebug("[CoreFtp] {line}", line);
            yield return line;
        }
    }
}
