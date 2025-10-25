using CoreFtp.Components.DirectoryListing.Parser;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CoreFtp.Components.DirectoryListing;

internal sealed class ListDirectoryProvider : DirectoryProviderBase
{
    private readonly List<IListDirectoryParser> _directoryParsers;

    public ListDirectoryProvider(Encoding encoding, ILogger? logger, Infrastructure.Stream.FtpControlStream stream)
        : base(logger, encoding, stream)
    {
        _directoryParsers = new List<IListDirectoryParser>
        {
            new UnixDirectoryParser(),
            new DosDirectoryParser(),
        };
    }

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default)
        => ListNodesAsync(cancellationToken: cancellationToken);

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => ListNodesAsync(FtpNodeType.File, sortBy, cancellationToken);

    public override IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => ListNodesAsyncEnum(FtpNodeType.File, sortBy, cancellationToken);

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
        => ListNodesAsync(FtpNodeType.Directory, cancellationToken: cancellationToken);

    /// <summary>
    /// Lists all nodes (files and directories) in the current working directory
    /// </summary>
    /// <param name="ftpNodeType"></param>
    /// <returns></returns>
    private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodesAsync(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null,
        CancellationToken cancellationToken = default)
    {
        Logger?.LogDebug("[CoreFtp] ListDirectoryProvider: Listing {ftpNodeType}", ftpNodeType);

        try
        {
            bool first = true;
            var nodes = new List<FtpNodeInformation>();
            IListDirectoryParser? parser = null;
            await foreach (var line in RetrieveDirectoryListingAsyncEnum(cancellationToken))
            {
                if (first)
                {
                    first = false;
                    parser = _directoryParsers.FirstOrDefault(parser => parser.Test(line));
                }

                if (parser == null)
                    break;

                var parsed = parser.Parse(line);

                if (parsed != null && (ftpNodeType.HasValue || parsed.NodeType == ftpNodeType))
                    nodes.Add(parsed);
            }

            return nodes.AsReadOnly();
        }
        finally
        {
            FtpStream.Dispose(true);
        }
    }

    private async IAsyncEnumerable<FtpNodeInformation> ListNodesAsyncEnum(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            Logger?.LogDebug("[CoreFtp] ListDirectoryProvider: Listing {ftpNodeType}", ftpNodeType);

            bool first = true;
            var nodes = new List<FtpNodeInformation>();
            IListDirectoryParser? parser = null;
            await foreach (var line in RetrieveDirectoryListingAsyncEnum(cancellationToken))
            {
                if (first)
                {
                    first = false;
                    parser = _directoryParsers.FirstOrDefault(parser => parser.Test(line));
                }

                if (parser == null)
                    break;

                var parsed = parser.Parse(line);
                if (parsed != null && (ftpNodeType.HasValue || parsed.NodeType == ftpNodeType))
                    yield return parsed;
            }
        }
        finally
        {
            FtpStream.Dispose(true);
        }
    }
}
