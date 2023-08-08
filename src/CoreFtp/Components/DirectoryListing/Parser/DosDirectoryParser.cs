using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CoreFtp.Components.DirectoryListing.Parser;

public sealed class DosDirectoryParser : IListDirectoryParser
{
    private readonly Regex dosDirectoryRegex = new(@"(?<modify>\d+-\d+-\d+\s+\d+:\d+\w+)\s+<DIR>\s+(?<name>.*)$", RegexOptions.Compiled);
    private readonly Regex dosFileRegex = new(@"(?<modify>\d+-\d+-\d+\s+\d+:\d+\w+)\s+(?<size>\d+)\s+(?<name>.*)$", RegexOptions.Compiled);

    public bool Test(string testString)
    {
        return dosDirectoryRegex.Match(testString).Success ||
               dosFileRegex.Match(testString).Success;
    }

    public FtpNodeInformation Parse(string line)
    {
        var directoryMatch = dosDirectoryRegex.Match(line);
        if (directoryMatch.Success)
        {
            return ParseDirectory(directoryMatch);
        }


        var fileMatch = dosFileRegex.Match(line);
        if (fileMatch.Success)
        {
            return ParseFile(fileMatch);
        }

        return null;
    }

    public static FtpNodeInformation ParseDirectory(Match match)
    {
        return new FtpNodeInformation
        {
            NodeType = FtpNodeType.Directory,
            Name = match.Groups["name"].Value,
            DateModified = match.Groups["modify"].Value.ExtractFtpDate(DateTimeStyles.AssumeLocal),
            Size = 0
        };
    }

    public static FtpNodeInformation ParseFile(Match match)
    {
        return new FtpNodeInformation
        {
            NodeType = FtpNodeType.File,
            Name = match.Groups["name"].Value,
            DateModified = match.Groups["modify"].Value.ExtractFtpDate(DateTimeStyles.AssumeLocal),
            Size = match.Groups["size"].Value.ParseOrDefault()
        };
    }
}
