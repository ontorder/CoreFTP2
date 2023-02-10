using CoreFtp;
using CoreFtp.Enum;
using System.Globalization;

Console.WriteLine("Ftp test");

var ftp = new FtpClient(new FtpClientConfiguration
{
    Host = "ftp.serenissima.tv",
    Password = "garavot",
    Username = "garavot"
});
await ftp.LoginAsync();
await foreach (var ftpFile in ftp.ListFilesAsyncEnum(DirSort.ModifiedTimestampReverse))
    Console.WriteLine($"{ftpFile.DateModified} - {ftpFile.Name}");
