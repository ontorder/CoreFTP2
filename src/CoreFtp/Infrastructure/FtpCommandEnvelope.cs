namespace CoreFtp.Infrastructure;

using Enum;
using Extensions;

public sealed class FtpCommandEnvelope
{
    public FtpCommand FtpCommand { get; set; }
    public string? Data { get; set; } = null;

    public FtpCommandEnvelope(FtpCommand ftpCommand)
        => FtpCommand = ftpCommand;

    public FtpCommandEnvelope(FtpCommand ftpCommand, string data)
    {
        FtpCommand = ftpCommand;
        Data = data;
    }

    public string GetCommandString()
    {
        string command = FtpCommand.ToString();

        return Data.IsNullOrEmpty()
            ? command
            : $"{command} {Data}";
    }
}
