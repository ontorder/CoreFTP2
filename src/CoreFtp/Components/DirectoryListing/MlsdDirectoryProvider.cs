using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CoreFtp.Components.DirectoryListing;

internal sealed class MlsdDirectoryProvider : DirectoryProviderBase
{
    public MlsdDirectoryProvider(FtpClient ftpClient, ILogger logger, FtpClientConfiguration configuration)
    {
        FtpClient = ftpClient;
        Configuration = configuration;
        Logger = logger;
    }

    private void EnsureLoggedIn()
    {
        if (!FtpClient.IsConnected || !FtpClient.IsAuthenticated)
            throw new FtpException("User must be logged in");
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await FtpClient.DataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(cancellationToken: cancellationToken);
        }
        finally
        {
            FtpClient.DataSocketSemaphore.Release();
        }
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await FtpClient.DataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(FtpNodeType.Directory, cancellationToken);
        }
        finally
        {
            FtpClient.DataSocketSemaphore.Release();
        }
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await FtpClient.DataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(FtpNodeType.File, cancellationToken);
        }
        finally
        {
            FtpClient.DataSocketSemaphore.Release();
        }
    }

    public override async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await FtpClient.DataSocketSemaphore.WaitAsync(cancellationToken);
            await foreach (var node in ListNodesAsyncEnum(FtpNodeType.File, sortBy, cancellationToken))
                yield return node;
        }
        finally
        {
            FtpClient.DataSocketSemaphore.Release();
        }
    }

    private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodeTypeAsync(FtpNodeType? ftpNodeType = null, CancellationToken cancellationToken = default)
    {
        string nodeTypeString = !ftpNodeType.HasValue
            ? "all"
            : ftpNodeType.Value == FtpNodeType.File
                ? "file"
                : "dir";

        Logger?.LogDebug("[MlsdDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        EnsureLoggedIn();

        try
        {
            Stream = await FtpClient.ConnectDataStreamAsync(cancellationToken);
            if (Stream == null)
                throw new FtpException("Could not establish a data connection");

            var result = await FtpClient.ControlStream.SendCommandAsync(FtpCommand.MLSD, cancellationToken);
            if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData) && (result.FtpStatusCode != FtpStatusCode.ClosingData))
                throw new FtpException("Could not retrieve directory listing " + result.ResponseMessage);

            var nodes = new List<FtpNodeInformation>();
            await foreach(var node in RetrieveDirectoryListingAsyncEnum(cancellationToken))
            {
                if (node.IsNullOrWhiteSpace()) continue;
                if (ftpNodeType == null || false == node.Contains($"type={nodeTypeString}")) continue;
                nodes.Add(node.ToFtpNode());
            }

            return nodes.AsReadOnly();
        }
        finally
        {
            Stream?.Dispose();
            Stream = null;
        }
    }

    private async IAsyncEnumerable<FtpNodeInformation> ListNodesAsyncEnum(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string nodeTypeString = !ftpNodeType.HasValue
            ? "all"
            : ftpNodeType.Value == FtpNodeType.File
                ? "file"
                : "dir";

        EnsureLoggedIn();
        Logger?.LogDebug("[MlsdDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        try
        {
            Stream = await FtpClient.ConnectDataStreamAsync(cancellationToken);
            var result = await FtpClient.ControlStream.SendCommandAsync(new FtpCommandEnvelope(FtpCommand.MLSD), cancellationToken);

            if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData))
                throw new FtpException("Could not retrieve directory listing: " + result.ResponseMessage);

            var nodes = new List<FtpNodeInformation>();
            await foreach (var line in RetrieveDirectoryListingAsyncEnum(cancellationToken))
            {
                if (line.IsNullOrWhiteSpace()) continue;
                if (ftpNodeType.HasValue && !line.Contains($"type={nodeTypeString}")) continue;
                nodes.Add(line.ToFtpNode());
            }

            IEnumerable<FtpNodeInformation> sortedNodes = sortBy switch
            {
                DirSort.Alphabetical => nodes.OrderBy(no => no.Name),
                DirSort.AlphabeticalReverse => nodes.OrderByDescending(no => no.Name),
                DirSort.ModifiedTimestampReverse => nodes.OrderByDescending(no => no.DateModified),
                null => nodes,
                _ => throw new Exception(),
            };
            foreach (var node in sortedNodes)
                yield return node;
        }
        finally
        {
            Stream.Dispose();
        }
    }
}
