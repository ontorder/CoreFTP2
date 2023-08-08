using CoreFtp.Components.DirectoryListing.Parser;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CoreFtp.Components.DirectoryListing;

internal sealed class ListDirectoryProvider : DirectoryProviderBase
{
    private readonly List<IListDirectoryParser> _directoryParsers;

    public ListDirectoryProvider(FtpClient ftpClient, ILogger logger, FtpClientConfiguration configuration)
    {
        _ftpClient = ftpClient;
        _logger = logger;
        _configuration = configuration;

        _directoryParsers = new List<IListDirectoryParser>
        {
            new UnixDirectoryParser(logger),
            new DosDirectoryParser(logger),
        };
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
            return await ListNodesAsync(cancellationToken: cancellationToken);
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
            return await ListNodesAsync(FtpNodeType.File, sortBy, cancellationToken);
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

    public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _ftpClient.dataSocketSemaphore.WaitAsync(cancellationToken);
            return await ListNodesAsync(FtpNodeType.Directory, cancellationToken: cancellationToken);
        }
        finally
        {
            _ftpClient.dataSocketSemaphore.Release();
        }
    }

    /// <summary>
    /// Lists all nodes (files and directories) in the current working directory
    /// </summary>
    /// <param name="ftpNodeType"></param>
    /// <returns></returns>
    private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodesAsync(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        _logger?.LogDebug("[ListDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        try
        {
            _stream = await _ftpClient.ConnectDataStreamAsync(cancellationToken);
            string arguments = sortBy switch
            {
                DirSort.Alphabetical => "-1",
                DirSort.AlphabeticalReverse => "-r",
                DirSort.ModifiedTimestampReverse => "-t",
                _ => String.Empty,
            };
            var result = await _ftpClient.ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.LIST,
                Data = arguments
            }, cancellationToken);

            if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData))
                throw new FtpException("Could not retrieve directory listing " + result.ResponseMessage);

            bool first = true;
            var nodes = new List<FtpNodeInformation>();
            IListDirectoryParser parser = null;
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

                if (parsed != null && ftpNodeType.HasValue || parsed.NodeType == ftpNodeType)
                    nodes.Add(parsed);
            }

            return nodes.AsReadOnly();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private async IAsyncEnumerable<FtpNodeInformation> ListNodesAsyncEnum(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        _logger?.LogDebug("[ListDirectoryProvider] Listing {ftpNodeType}", ftpNodeType);

        try
        {
            _stream = await _ftpClient.ConnectDataStreamAsync(cancellationToken);
            string arguments = sortBy switch
            {
                DirSort.Alphabetical => "-1",
                DirSort.AlphabeticalReverse => "-r",
                DirSort.ModifiedTimestampReverse => "-t",
                _ => String.Empty,
            };
            var result = await _ftpClient.ControlStream.SendCommandAsync(new FtpCommandEnvelope
            {
                FtpCommand = FtpCommand.LIST,
                Data = arguments
            }, cancellationToken);

            if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData))
                throw new FtpException("Could not retrieve directory listing: " + result.ResponseMessage);

            bool first = true;
            var nodes = new List<FtpNodeInformation>();
            IListDirectoryParser parser = null;
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

                if (parsed != null && ftpNodeType.HasValue || parsed.NodeType == ftpNodeType)
                    yield return parsed;
            }
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
