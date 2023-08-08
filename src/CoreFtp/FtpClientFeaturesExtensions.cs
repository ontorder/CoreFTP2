using System.Linq;

namespace CoreFtp;

public static class FtpClientFeaturesExtensions
{
    public static bool UsesMlsd(this FtpClient operand)
    {
        return (operand.Features != null) && operand.Features.Any(x => x == "MLSD");
    }

    public static bool UsesEpsv(this FtpClient operand)
    {
        return (operand.Features != null) && operand.Features.Any(x => x == "EPSV");
    }

    public static bool UsesPasv(this FtpClient operand)
    {
        return (operand.Features != null) && operand.Features.Any(x => x == "PASV");
    }
}
