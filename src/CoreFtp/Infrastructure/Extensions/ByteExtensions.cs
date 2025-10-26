using System.Text;

namespace CoreFtp.Infrastructure.Extensions;

public static class ByteExtensions
{
    public static byte[] ToAsciiBytes(this string operand)
    {
        return Encoding.ASCII.GetBytes($"{operand}\r\n".ToCharArray());
    }
}
