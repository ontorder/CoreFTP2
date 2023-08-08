using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreFtp.Components.DirectoryListing;

#nullable enable

public abstract class DirectoryProviderBase : IDirectoryProvider
{
    protected FtpClientConfiguration? Configuration;
    protected FtpClient? FtpClient;
    protected ILogger? Logger;
    protected Stream? Stream;

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public virtual IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    protected async Task<IEnumerable<string>> RetrieveDirectoryListingAsync(CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(FtpClient.ControlStream.Encoding, cancellationToken);
        Logger?.LogDebug("{lines}", lines);
        return lines;
    }

    protected async IAsyncEnumerable<string> RetrieveDirectoryListingAsyncEnum([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (string line in ReadLineAsyncEnum(FtpClient.ControlStream.Encoding, cancellationToken))
        {
            Logger?.LogDebug("{line}", line);
            yield return line;
        }
    }

    protected async Task<ICollection<string>> ReadLinesAsync(Encoding encoding, CancellationToken cancellationToken)
    {
        const int MaxReadSize = 1024;

        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        var data = new ArrayBufferWriter<byte>();
        int count;
        do
        {
            var buffer = new byte[MaxReadSize];
            count = await Stream.ReadAsync(new Memory<byte>(buffer), cancellationToken);
            if (count == 0) break;
            data.Write(buffer.AsSpan()[..count]);
        } while (count == MaxReadSize);

        return SplitEncode(data.WrittenSpan, encoding);
    }

    // TODO use Pipe
    protected async IAsyncEnumerable<string> ReadLineAsyncEnum(Encoding encoding, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int MaxReadSize = 512;

        if (encoding == null)
            throw new ArgumentNullException(nameof(encoding));

        var data = new ArrayBufferWriter<byte>();
        PartialSplitStatus split_status = new();
        int count;

        do
        {
            var buf = new byte[MaxReadSize];
            count = await Stream.ReadAsync(buf, cancellationToken);
            if (count == 0) break;
            data.Write(buf.AsSpan()[..count]);

            foreach (string line in SplitEncodePartial(data.WrittenSpan, encoding, split_status))
                yield return line;
        }
        while (count == MaxReadSize);
    }

    public static ICollection<string> SplitEncode(ReadOnlySpan<byte> writtenSpan, Encoding encoding)
    {
        List<string> lines = new(1);
        int prev_pos = 0;

        do
        {
            int newline_pos = writtenSpan[prev_pos..].IndexOf((byte)'\n');
            if (newline_pos == -1)
            {
                if (writtenSpan.Length > 0) throw new InvalidOperationException("non-terminated data");
                break;
            }
            newline_pos += prev_pos;
            var token = writtenSpan[prev_pos..newline_pos].TrimEnd((byte)'\r');
            string encoded = encoding.GetString(token);
            lines.Add(encoded);
            prev_pos = newline_pos + 1;
        } while (prev_pos < writtenSpan.Length);

        return lines;
    }

    public static ICollection<string> SplitEncodePartial(ReadOnlySpan<byte> writtenSpan, Encoding encoding, PartialSplitStatus status)
    {
        List<string> lines = new(1);

        do
        {
            int newline_pos = writtenSpan[status.PreviousPosition..].IndexOf((byte)'\n');
            if (newline_pos == -1) break;
            newline_pos += status.PreviousPosition;
            var token = writtenSpan[status.PreviousPosition..newline_pos].TrimEnd((byte)'\r');
            string encoded = encoding.GetString(token);
            lines.Add(encoded);
            status.PreviousPosition = newline_pos + 1;
        } while (status.PreviousPosition < writtenSpan.Length);

        return lines;
    }

    public class PartialSplitStatus
    {
        public int PreviousPosition = 0;
    }
}
