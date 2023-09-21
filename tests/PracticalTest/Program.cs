using CoreFtp;
using CoreFtp.Enum;

Console.WriteLine("Ftp test");

//var cfg = new FtpClientConfiguration { Host = "ftp.serenissima.tv", Password = "garavot", Username = "garavot" };
var cfg = new FtpClientConfiguration { Host = "192.168.2.230", Password = "AC==!2013", Username = "ac001bu" };
var ftp = new FtpClient(cfg);
await ftp.LoginAsync(default);
await foreach (var ftpFile in ftp.ListFilesAsyncEnum(DirSort.ModifiedTimestampReverse, default))
    Console.WriteLine($"{ftpFile.DateModified} - {ftpFile.Name}");
;
