using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreFtp.Components.DirectoryListing;

internal abstract class DirectoryProviderBase : IDirectoryProvider
{
    protected FtpClientConfiguration _configuration;
    protected FtpClient _ftpClient;
    protected ILogger _logger;
    protected Stream _stream;

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public virtual IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    protected IEnumerable<string> RetrieveDirectoryListing()
    {
        string line;
        while ((line = ReadLine(_ftpClient.ControlStream.Encoding)) != null)
        {
            _logger?.LogDebug("{line}", line);
            yield return line;
        }
    }

    protected async IAsyncEnumerable<string> RetrieveDirectoryListingAsyncEnum([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string line;
        while ((line = await ReadLineAsync(_ftpClient.ControlStream.Encoding, cancellationToken)) != null)
        {
            _logger?.LogDebug("{line}", line);
            yield return line;
        }
    }

    protected string ReadLine(Encoding encoding)
    {
        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        var data = new List<byte>();
        var buf = new byte[1];
        string line = null;

        while (_stream.Read(buf, 0, buf.Length) > 0)
        {
            data.Add(buf[0]);
            if ((char)buf[0] != '\n')
                continue;
            line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
            break;
        }

        return line;
    }

    protected async Task<string> ReadLineAsync(Encoding encoding, CancellationToken cancellationToken)
    {
        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        var data = new List<byte>();
        var buf = new byte[1];
        string line = null;

        // TODO don't read 1 byte pls; make async enumerable?
        while (await _stream.ReadAsync(buf, cancellationToken) > 0)
        {
            data.Add(buf[0]);
            if ((char)buf[0] != '\n')
                continue;
            line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
            break;
        }

        return line;
    }
}
