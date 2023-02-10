using CoreFtp.Components.DirectoryListing.Parser;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace CoreFtp.Components.DirectoryListing
{
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

        public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync()
        {
            try
            {
                await _ftpClient.dataSocketSemaphore.WaitAsync();
                return await ListNodesAsync();
            }
            finally
            {
                _ftpClient.dataSocketSemaphore.Release();
            }
        }

        public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null)
        {
            try
            {
                await _ftpClient.dataSocketSemaphore.WaitAsync();
                return await ListNodesAsync(FtpNodeType.File, sortBy);
            }
            finally
            {
                _ftpClient.dataSocketSemaphore.Release();
            }
        }

        public override async IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null)
        {
            try
            {
                await _ftpClient.dataSocketSemaphore.WaitAsync();
                await foreach (var v in ListNodesAsyncEnum(FtpNodeType.File, sortBy))
                    yield return v;
            }
            finally
            {
                _ftpClient.dataSocketSemaphore.Release();
            }
        }

        public override async Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync()
        {
            try
            {
                await _ftpClient.dataSocketSemaphore.WaitAsync();
                return await ListNodesAsync(FtpNodeType.Directory);
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
        private async Task<ReadOnlyCollection<FtpNodeInformation>> ListNodesAsync(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null)
        {
            EnsureLoggedIn();
            _logger?.LogDebug($"[ListDirectoryProvider] Listing {ftpNodeType}");

            try
            {
                _stream = await _ftpClient.ConnectDataStreamAsync();

                string arguments;
                switch (sortBy)
                {
                    case DirSort.Alphabetical: arguments = "-1"; break; // -S ???
                    case DirSort.AlphabeticalReverse: arguments = "-r"; break;
                    case DirSort.ModifiedTimestampReverse: arguments = "-t"; break;

                    default:
                    case null: arguments = String.Empty; break;
                }

                var result = await _ftpClient.ControlStream.SendCommandAsync(new FtpCommandEnvelope
                {
                    FtpCommand = FtpCommand.LIST,
                    Data = arguments
                });

                if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData))
                    throw new FtpException("Could not retrieve directory listing " + result.ResponseMessage);

                bool first = true;
                var nodes = new List<FtpNodeInformation>();
                IListDirectoryParser parser = null;
                await foreach (var line in RetrieveDirectoryListingAsyncEnum())
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

        private async IAsyncEnumerable<FtpNodeInformation> ListNodesAsyncEnum(FtpNodeType? ftpNodeType = null, DirSort? sortBy = null)
        {
            EnsureLoggedIn();
            _logger?.LogDebug($"[ListDirectoryProvider] Listing {ftpNodeType}");

            try
            {
                _stream = await _ftpClient.ConnectDataStreamAsync();
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
                });

                if ((result.FtpStatusCode != FtpStatusCode.DataAlreadyOpen) && (result.FtpStatusCode != FtpStatusCode.OpeningData))
                    throw new FtpException("Could not retrieve directory listing: " + result.ResponseMessage);

                bool first = true;
                var nodes = new List<FtpNodeInformation>();
                IListDirectoryParser parser = null;
                await foreach (var line in RetrieveDirectoryListingAsyncEnum())
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
}
