namespace CoreFtp.Enum;

public static class FtpStatusCodeExtensions
{
    public static bool IsError(this CFtpStatusCode statusCode)
    {
        var codeValue = (int)statusCode;
        return (codeValue >= 400 && codeValue <= 599) || statusCode == CFtpStatusCode.Code0Undefined;
    }

    public static bool IsSuccess(this CFtpStatusCode statusCode)
    {
        var codeValue = (int)statusCode;
        return codeValue >= 100 && codeValue < 400;
    }
}
