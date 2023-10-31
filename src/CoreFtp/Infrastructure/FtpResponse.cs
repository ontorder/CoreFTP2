namespace CoreFtp.Infrastructure;

#nullable enable

using Enum;

public sealed class FtpResponse
{
    public string[] Data { get; set; }

    public static FtpResponse EmptyResponse = new()
    {
        ResponseMessage = "No response was received",
        FtpStatusCode = FtpStatusCode.Undefined
    };

    public FtpStatusCode FtpStatusCode { get; set; }

    public bool IsSuccess
    {
        get
        {
            int statusCode = (int)FtpStatusCode;
            return statusCode >= 100 && statusCode < 400;
        }
    }

    public string Request { get; set; }

    public string ResponseMessage { get; set; }
}
