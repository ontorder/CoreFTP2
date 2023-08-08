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

namespace CoreFtp.Components.DirectoryListing;

internal sealed class MlsdDirectoryProvider : DirectoryProviderBase
{
    public MlsdDirectoryProvider(FtpClient ftpClient, ILogger logger, FtpClientConfiguration configuration)
    {
        _ftpClient = ftpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private void EnsureLoggedIn()
    {
        if (!_ftpClient.IsConnected || !_ftpClient.IsAuthenticated)
            throw new FtpException("User must be logged in");
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _ftpClient.dataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(cancellationToken: cancellationToken);
        }
        finally
        {
            _ftpClient.dataSocketSemaphore.Release();
        }
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _ftpClient.dataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(FtpNodeType.Directory, cancellationToken);
        }
        finally
        {
            _ftpClient.dataSocketSemaphore.Release();
        }
    }

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await _ftpClient.dataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodeTypeAsync(FtpNodeType.File, cancellationToken);
        }
        finally
        {
            _ftpClient.dataSocketSemaphore.Release();
        }
    }

    public override async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await _ftpClient.dataSocketSemaphore.WaitAsync(cancellationToken);
            await foreach (var v in ListNodesAsyncEnum(FtpNodeType.File, sortBy, cancellationToken))
                yield return v;
        }
        finally
        {
            _ftpClient.dataSocketSemaphore.Release();
        }
    }

    private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodeTypeAsync(FtpNodeType? ftpNodeType = null, CancellationToken cancellationToken = default)
    {
        string nodeTypeString = !ftpNodeType.HasValue
            ? "all"
            : ftpNodeType.Value == FtpNodeType.File
                ? "file"
                : "dir";

        _logger?.LogDebug("[MlsdDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        EnsureLoggedIn();

        try
        {
            _stream = await _ftpClient.ConnectDataStreamAsync(cancellationToken);
            if (_stream == null)
                throw new FtpException("Could not establish a data connection");

            var result = await _ftpClient.ControlStream.SendCommandAsync(FtpCommand.MLSD, cancellationToken);
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
            _stream?.Dispose();
            _stream = null;
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
        _logger?.LogDebug("[MlsdDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        try
        {
            _stream = await _ftpClient.ConnectDataStreamAsync(cancellationToken);
            var result = await _ftpClient.ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.MLSD,
                Data = null
            }, cancellationToken);

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
            _stream.Dispose();
        }
    }
}
