using CoreFtp.Enum;
using CoreFtp.Infrastructure.Extensions;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CoreFtp;

public partial class FtpModelParser
{
    private static readonly Regex _pwdParser = CreatePwdRegex();
    private static readonly Regex _stdParser = CreateStdStatusRegex();

    private enum FeatsParseStates
    {
        Header,
        Body
    }

    public static async Task<bool> ParseCwdAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        return status?.Code == (int)FtpStatusCode.FileActionOK;
    }

    public static async Task<(bool Ok, int? Port)> ParseEpsvAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return (false, null);
        if (status.Value.Code != (int)FtpStatusCode.EnteringExtendedPassive) return (false, null);

        var regex = RegexParseEpsvPort();
        var match = regex.Match(status.Value.Msg);
        if (match.Success == false) return (false, null);

        int port = int.Parse(match.Groups["PortNumber"].Value);
        return (true, port);
    }

    public static async Task<(bool Ok, ICollection<string>? Feats)> ParseFeatsAsync(IAsyncEnumerable<string> reader)
    {
        var parserState = FeatsParseStates.Header;
        List<string> feats = new(9);

        await foreach (string line in reader)
        {
            if (parserState == FeatsParseStates.Header)
            {
                if (line != "211-Features:") throw new InvalidOperationException();

                var checkStatus = ParseStatus(line);
                switch (checkStatus?.Code)
                {
                    case null: break;
                    case (int)FtpStatusCode.CommandNotImplemented: return (true, Array.Empty<string>());
                    case (int)FtpStatusCode.CommandSyntaxError: return (true, Array.Empty<string>());
                }

                parserState = FeatsParseStates.Body;
                continue;
            }

            if (parserState == FeatsParseStates.Body)
            {
                var checkStatus = ParseStatus(line);
                if (checkStatus.HasValue)
                {
                    if (checkStatus.Value.Code != (int)FtpStatusCode.EndFeats) throw new InvalidOperationException();
                    return (true, feats);
                }
                feats.Add(line);
                continue;
            }
        }
        return (false, null);
    }

    public static async Task<bool> ParseListAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return false;
        return status.Value.Code == (int)FtpStatusCode.DataAlreadyOpen || status.Value.Code == (int)FtpStatusCode.OpeningData;
    }

    public static async Task<bool> ParseMlsdAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        _ = await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return false;
        return status.Value.Code == (int)FtpStatusCode.DataAlreadyOpen || status.Value.Code == (int)FtpStatusCode.OpeningData;
    }

    public static async Task<(bool Ok, ICollection<string>? Motd)> ParseMotdAsync(IAsyncEnumerable<string> reader)
    {
        // 220-FileZilla Server 1.6.1
        // 220 Please visit https://filezilla-project.org/

        var lines = new List<string>();
        await foreach (string line in reader)
        {
            lines.Add(line);
            if (line.StartsWith("220-")) continue;
            var status = ParseStatus(line);
            if (status == null) return (false, lines);
            if (status.Value.Code != (int)FtpStatusCode.SendUserCommand) return (false, lines);
            break;
        }
        return (true, lines);
    }

    public static async Task<bool> ParseOptsAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        // 200 Always in UTF8 mode.
        return status?.Code == (int)FtpStatusCode.CommandOK;
    }

    public static async Task<bool> ParsePassAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        return status?.Code == (int)FtpStatusCode.LoggedInProceed;
    }

    public static async Task<(bool Ok, int? Port)> ParsePasvAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return (false, null);
        if (status.Value.Code == (int)FtpStatusCode.EnteringPassive) return (false, null);
        return (true, status.Value.Msg.ExtractPasvPortNumber());
    }

    public static async Task<(bool Ok, string? Path)> ParsePwdAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var parsed = _pwdParser.Match(e.Current);
        if (parsed.Success == false) return (false, null);
        string ftpStatusCodeString = parsed.Groups["statusCode"].Value;
        int ftpStatusCode = int.Parse(ftpStatusCodeString);
        if (ftpStatusCode != (int)FtpStatusCode.PathnameCreated) return (false, null);
        string ftpPath = parsed.Groups["path"].Value;
        return (true, ftpPath);
    }

    private static (int Code, string Msg)? ParseStatus(string statusString)
    {
        var match = _stdParser.Match(statusString);
        if (false == match.Success)
            return null;

        string ftpStatusCode = match.Groups["statusCode"].Value;
        string responseMessage = match.Groups["message"].Value;

        return (int.Parse(ftpStatusCode), responseMessage);
    }

    public static async Task<bool> ParseTypeAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        return status?.Code == (int)FtpStatusCode.CommandOK;
    }

    public static async Task<bool> ParseUserAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return false;
        return status.Value.Code == (int)FtpStatusCode.SendPasswordCommand
            || status.Value.Code == (int)FtpStatusCode.SendUserCommand
            || status.Value.Code == (int)FtpStatusCode.LoggedInProceed;
    }

    [GeneratedRegex("^(?<statusCode>[0-9]{3}) (?<message>.*)$", RegexOptions.Compiled)]
    private static partial Regex CreateStdStatusRegex();

    [GeneratedRegex("^(?<statusCode>[0-9]{3}) \"(?<path>.*)\".*$", RegexOptions.Compiled)]
    private static partial Regex CreatePwdRegex();

    [GeneratedRegex("(?:[\\|,])(?<PortNumber>\\d+)(?:[\\|,])")]
    private static partial Regex RegexParseEpsvPort();
}
