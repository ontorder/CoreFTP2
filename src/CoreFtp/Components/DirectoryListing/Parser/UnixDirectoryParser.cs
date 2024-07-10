using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
#nullable enable
namespace CoreFtp.Components.DirectoryListing.Parser;

public sealed partial class UnixDirectoryParser : IListDirectoryParser
{
    private readonly List<Regex> unixRegexList = new()
    {
        CreateUnixDirRegex(),
        CreateStingrayUnixDirRegex(),
    };

    public bool Test(string testString)
    {
        foreach (var expression in unixRegexList)
        {
            if (expression.Match(testString).Success) return true;
        }

        return false;
    }

    public FtpNodeInformation? Parse(string line)
    {
        var matches = Match.Empty;
        foreach (var expression in unixRegexList)
        {
            if (false == expression.Match(line).Success) continue;
            matches = expression.Match(line);
            break;
        }

        if (matches.Success == false)
            return null;

        var node = new FtpNodeInformation
        {
            NodeType = DetermineNodeType(matches.Groups["permissions"]),
            Name = DetermineName(matches.Groups["name"]),
            DateModified = DetermineDateModified(matches.Groups["date"]),
            Size = DetermineSize(matches.Groups["size"])
        };

        return node;
    }

    private static FtpNodeType DetermineNodeType(Capture permissions)
    {
        // No permissions means we can't determine the node type
        if (permissions.Value.Length == 0)
            throw new InvalidDataException("No permissions found");

        return permissions.Value[0] switch
        {
            'd' => FtpNodeType.Directory,
            '-' or 's' => FtpNodeType.File,
            'l' => FtpNodeType.SymbolicLink,
            _ => throw new InvalidDataException("Unexpected data format"),
        };
    }

    private static string DetermineName(Capture name)
    {
        if (name.Value.Length == 0)
            throw new InvalidDataException("No name found");

        return name.Value;
    }

    private static DateTime DetermineDateModified(Capture name)
    {
        return name.Value.Length == 0
            ? DateTime.MinValue
            : name.Value.ExtractFtpDate(DateTimeStyles.AssumeLocal);
    }

    private static long DetermineSize(Capture sizeGroup)
    {
        if (sizeGroup.Value.Length == 0)
            return 0;


        return long.TryParse(sizeGroup.Value, out long size)
            ? size
            : 0;
    }

    [GeneratedRegex(
        @"(?<permissions>.+)\s+" +
        @"(?<objectcount>\d+)\s+" +
        @"(?<user>.+)\s+" +
        @"(?<group>.+)\s+" +
        @"(?<size>\d+)\s+" +
        @"(?<date>\w+\s+\d+\s+\d+:\d+|\w+\s+\d+\s+\d+)\s+" +
        @"(?<name>.*)$",
        RegexOptions.Compiled)]
    private static partial Regex CreateUnixDirRegex();

    [GeneratedRegex(
        @"(?<permissions>.+)\s+" +
        @"(?<objectcount>\d+)\s+" +
        @"(?<size>\d+)\s+" +
        @"(?<date>\w+\s+\d+\s+\d+:\d+|\w+\s+\d+\s+\d+)\s+" +
        @"(?<name>.*)$",
        RegexOptions.Compiled)]
    private static partial Regex CreateStingrayUnixDirRegex();
}
