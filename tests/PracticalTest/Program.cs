using CoreFtp;
using CoreFtp.Enum;
using Microsoft.Extensions.Logging;

Console.WriteLine("Ftp test");

var cfg = new FtpClientConfiguration { Host = "127.0.0.1", Port = 2021, Password = "p", Username = "u", TimeoutSeconds = 5 };
var ftp = new FtpClient(cfg, new DebugLogger());
try
{
    await ftp.LoginAsync(default);
    int count = 0;
    await foreach (var ftpFile in ftp.ListFilesAsyncEnum(DirSort.ModifiedTimestampReverse, default))
    {
        Console.Write('*');
        //Console.WriteLine($"{ftpFile.DateModified} - {ftpFile.Name}");
        //await Task.Delay(1);
        ++count;
    }
    Console.WriteLine($"count {count}");

    //var d = await ftp.OpenFileReadStreamAsync("HD NOTIZIE OGGI - REGIA TG HD_20251026045942.xml", default);
    //using var reader = new System.IO.StreamReader(d);
    //var bla = await reader.ReadToEndAsync();
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
