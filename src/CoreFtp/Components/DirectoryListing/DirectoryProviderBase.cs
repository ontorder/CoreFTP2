using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using Microsoft.Extensions.Logging;

namespace CoreFtp.Components.DirectoryListing
{
    internal abstract class DirectoryProviderBase : IDirectoryProvider
    {
        protected FtpClient ftpClient;
        protected FtpClientConfiguration configuration;
        protected ILogger logger;
        protected Stream stream;

        public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListAllAsync() => throw new NotImplementedException();

        public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListDirectoriesAsync() => throw new NotImplementedException();

        public virtual IAsyncEnumerable<FtpNodeInformation> ListFilesAsyncEnum(DirSort? sortBy = null) => throw new NotImplementedException();

        public virtual Task<ReadOnlyCollection<FtpNodeInformation>> ListFilesAsync(DirSort? sortBy = null) => throw new NotImplementedException();

        protected IEnumerable<string> RetrieveDirectoryListing()
        {
            string line;
            while ((line = ReadLine(ftpClient.ControlStream.Encoding)) != null)
            {
                logger?.LogDebug(line);
                yield return line;
            }
        }

        protected async IAsyncEnumerable<string> RetrieveDirectoryListingAsyncEnum()
        {
            string line;
            while ((line = await ReadLineAsync(ftpClient.ControlStream.Encoding)) != null)
            {
                logger?.LogDebug(line);
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

            while (stream.Read(buf, 0, buf.Length) > 0)
            {
                data.Add(buf[0]);
                if ((char)buf[0] != '\n')
                    continue;
                line = encoding.GetString(data.ToArray()).Trim('\r', '\n');
                break;
            }

            return line;
        }

        protected async Task<string> ReadLineAsync(Encoding encoding)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            var data = new List<byte>();
            var buf = new byte[1];
            string line = null;

            while (await stream.ReadAsync(buf, 0, buf.Length) > 0)
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
}