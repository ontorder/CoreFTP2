using CoreFtp;
using CoreFtp.Enum;
using Microsoft.Extensions.Logging;

Console.WriteLine("Ftp test");

var cfg = new FtpClientConfiguration { Host = "ftp.serenissima.tv", Password = "garavot", Username = "garavot" };
//var cfg = new FtpClientConfiguration { Host = "192.168.2.230", Password = "AC==!2013", Username = "ac001bu", TimeoutSeconds = 5 };
//var cfg = new FtpClientConfiguration { Host = "ftp.streamcloud.it", Password = "AC==!2013", Username = "ac001bu", TimeoutSeconds = 5 };
//var cfg = new FtpClientConfiguration { Host = "127.0.0.1", Port = 2021, Password = "p", Username = "u", TimeoutSeconds = 5 };
var ftp = new FtpClient(cfg, new DebugLogger());
try
{
    await ftp.LoginAsync(default);

    await foreach (var ftpFile in ftp.ListFilesAsyncEnum(DirSort.ModifiedTimestampReverse, default))
        Console.WriteLine($"{ftpFile.DateModified} - {ftpFile.Name}");
}
catch (Exception generalErr)
{
    Console.WriteLine(generalErr.ToString());
}
await ftp.LogOutAsync(default);
;

class DebugLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        //string msg = formatter(state, exception);
        Console.WriteLine($"{logLevel}] {state}");
    }
}
