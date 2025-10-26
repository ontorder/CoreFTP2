namespace CoreFtp.Infrastructure.Extensions;

using Enum;

public static class FtpStringExtensions
{
    public static CFtpStatusCode ToStatusCode(this string operand)
    {
        _ = int.TryParse(operand[..3], out int statusCodeValue);
        return statusCodeValue.ToNullableEnum<CFtpStatusCode>() ?? CFtpStatusCode.Code0Undefined;
    }
}
