using CoreFtp.Enum;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
        var regex = RegexParsePasvPort();
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
        var regex = RegexParseEpsvPort();
        var match = regex.Match(operand);
        if (match.Success == false)
            return null;

        return int.Parse(match.Groups["PortNumber"].Value);
    }

    private static FtpNodeType ToNodeType(this string operand) => operand switch
    {
        "dir" => FtpNodeType.Directory,
        "file" => FtpNodeType.File,
        _ => FtpNodeType.SymbolicLink,
    };

    // TODO parsing protocol shouldn't be randomly in an extension method
    // TODO im pretty sure spaces are well defined and parsing shouldn't use trim
    // example: type=file;size=4222572089;modify=20251023213220.665;perms=awr; filename.MP4
    internal static FtpNodeInformation ToFtpNode(this string operand)
    {
        var tokens = operand.Split(';', 5).ToArray();
        if (tokens.Length != 5) throw new InvalidDataException(operand);

        var filename = tokens[4][0] == ' ' ? tokens[4][1..] : tokens[4];
        var nodeType = string.Empty;
        var dateModified = string.Empty;
        var size = string.Empty;
        for (var id = 0; id < 4; ++id)
        {
            var split = tokens[id].Split('=');
            switch (split[0][0])
            {
                case 't': nodeType = split[1]; break;
                case 'm': dateModified = split[1]; break;
                case 's': size = split[1]; break;
            }
        }

        return new FtpNodeInformation
        {
            NodeType = nodeType.Trim().ToNodeType(),
            Name = filename,
            Size = size.ParseOrDefault(),
            DateModified = dateModified.ParseExactOrDefault("yyyyMMddHHmmss", "yyyyMMddHHmmss.fff")
        };
    }

    [GeneratedRegex("(?:[\\|,])(?<PortNumber>\\d+)(?:[\\|,])")]
    private static partial Regex RegexParseEpsvPort();

    [GeneratedRegex("([0-9]{1,3}[|,]){1,}[0-9]{1,3}")]
    private static partial Regex RegexParsePasvPort();
}
