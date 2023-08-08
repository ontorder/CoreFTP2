using CoreFtp;
using CoreFtp.Enum;

Console.WriteLine("Ftp test");

var ftp = new FtpClient(new FtpClientConfiguration
{
    Host = "ftp.serenissima.tv",
    Password = "garavot",
    Username = "garavot"
});
await ftp.LoginAsync(default);
await foreach (var ftpFile in ftp.ListFilesAsyncEnum(DirSort.ModifiedTimestampReverse, default))
    Console.WriteLine($"{ftpFile.DateModified} - {ftpFile.Name}");
