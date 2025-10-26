using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace CoreFtp.Components.DirectoryListing;

internal sealed class MlsdDirectoryProvider : DirectoryProviderBase
{
    public MlsdDirectoryProvider(Encoding encoding, ILogger? logger, Infrastructure.Stream.FtpTextDataStream stream)
        : base(logger, encoding, stream)
    {
    }

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default)
        => ListNodeTypeAsync(cancellationToken: cancellationToken);

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
        => ListNodeTypeAsync(FtpNodeType.Directory, cancellationToken);

    public override Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => ListNodeTypeAsync(FtpNodeType.File, cancellationToken);

    public override IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default)
        => ListNodesAsyncEnum(FtpNodeType.File, sortBy, cancellationToken);

    private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodeTypeAsync(FtpNodeType? ftpNodeType = null, CancellationToken cancellationToken = default)
    {
        var nodeTypeString = ftpNodeType switch
        {
            null => "all",
            FtpNodeType.File => "file",
            FtpNodeType.Directory => "dir",
            _ => throw new ArgumentOutOfRangeException(nameof(ftpNodeType)),
        };

        Logger?.LogDebug("[CoreFtp] MlsdDirectoryProvider: Listing {ftpNodeType}", ftpNodeType);

        var nodes = new List<FtpNodeInformation>();
        await foreach (var node in DataStream.ReadLineAsyncEnum(cancellationToken))
        {
            if (node.IsNullOrWhiteSpace()) continue;
            if (ftpNodeType == null || false == node.Contains($"type={nodeTypeString}")) continue;
            nodes.Add(node.ToFtpNode());
        }

        return nodes.AsReadOnly();
    }

    private async IAsyncEnumerable<FtpNodeInformation> ListNodesAsyncEnum(
        FtpNodeType? ftpNodeType = null,
        DirSort? sortBy = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string nodeTypeString = ftpNodeType switch
        {
            null => "all",
            FtpNodeType.File => "file",
            FtpNodeType.Directory => "dir",
            _ => throw new ArgumentOutOfRangeException(nameof(ftpNodeType)),
        };

        IEnumerable<FtpNodeInformation> sortedNodes = Array.Empty<FtpNodeInformation>();

        try
        {
            Logger?.LogDebug("[CoreFtp] MlsdDirectoryProvider: Listing {ftpNodeType}", ftpNodeType);

            var nodes = new List<FtpNodeInformation>();
            await foreach (var line in DataStream.ReadLineAsyncEnum(cancellationToken))
            {
                if (line.IsNullOrWhiteSpace()) continue;
                if (ftpNodeType.HasValue && !line.Contains($"type={nodeTypeString}")) continue;
                nodes.Add(line.ToFtpNode());
            }

            sortedNodes = sortBy switch
            {
                DirSort.Alphabetical => nodes.OrderBy(no => no.Name),
                DirSort.AlphabeticalReverse => nodes.OrderByDescending(no => no.Name),
                DirSort.ModifiedTimestampReverse => nodes.OrderByDescending(no => no.DateModified),
                null => nodes,
                _ => throw new Exception(),
            };
        }
        catch (Exception readErr)
        {
            Logger?.LogError(readErr, "[CoreFtp] list nodes async enum EXCEPTION");
            yield break;
        }

        foreach (var node in sortedNodes)
            yield return node;
    }
}
