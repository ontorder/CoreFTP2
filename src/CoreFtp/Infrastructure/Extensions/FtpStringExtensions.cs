namespace CoreFtp.Infrastructure.Extensions;

using Enum;

public static class FtpStringExtensions
{
    public static FtpStatusCode ToStatusCode(this string operand)
    {
        _ = int.TryParse(operand[..3], out int statusCodeValue);
        return statusCodeValue.ToNullableEnum<FtpStatusCode>() ?? FtpStatusCode.Undefined;
    }
}
