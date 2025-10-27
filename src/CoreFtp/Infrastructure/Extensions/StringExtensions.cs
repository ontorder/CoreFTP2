namespace CoreFtp.Infrastructure.Extensions;

public static partial class StringExtensions
{
    internal static bool IsNullOrEmpty(this string operand)
        => string.IsNullOrEmpty(operand);

    internal static bool IsNullOrWhiteSpace(this string operand)
        => string.IsNullOrWhiteSpace(operand);

    internal static string CombineAsUriWith(this string operand, string rightHandSide)
        => string.Format("{0}/{1}", operand.TrimEnd('/'), rightHandSide.Trim('/'));

    internal static int? ExtractPasvPortNumber(this string operand)
    {
        var regex = FtpModelParser.CreatePasvPortRegex();
        var match = regex.Match(operand);

        if (!match.Success)
            return null;

        var values = match.Groups[0].Value.Split("|,".ToCharArray()).Select(int.Parse).ToArray();

        if (values.Length != 6)
            return null;

        // 5th and 6th values contain the port number
        return values[4] * 256 + values[5];
    }

    internal static int? ExtractEpsvPortNumber(this string operand)
    {
        var regex = FtpModelParser.CreateEpsvPortRegex();
        var match = regex.Match(operand);
        if (match.Success == false)
            return null;

        return int.Parse(match.Groups["PortNumber"].Value);
    }
}
