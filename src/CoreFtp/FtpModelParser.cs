using CoreFtp.Enum;
using CoreFtp.Infrastructure.Extensions;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CoreFtp;

public partial class FtpModelParser
{
    private CFtpStatusCode? _messageParsingState = null;
    private static readonly Regex _stdParser = CreateStdStatusRegex();
    private readonly List<string> _tempAdditionalData = new();
    private TaskCompletionSource<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> _waitResponse = new();

    public void Parse(string ftpLine)
    {
        var parsed = ParseFtpLine(ftpLine);

        switch (_messageParsingState)
        {
            case null:
                switch (ftpLine)
                {
                    case ['2', '1', '1', '-', ..]:
                        _messageParsingState = CFtpStatusCode.Code211Feats;
                        return;

                    case ['2', '2', '0', '-', ..]:
                        _messageParsingState = CFtpStatusCode.Code220Motd;
                        _tempAdditionalData.Add(ftpLine);
                        return;

                    case ['4', _, _, ' ', ..]:
                    case ['5', _, _, ' ', ..]:
                        var tempResponse2 = _waitResponse;
                        _waitResponse = new();
                        tempResponse2.TrySetResult((parsed.FtpStatusCode, parsed.ResponseMessage, null));
                        return;
                }

                Signal(parsed.FtpStatusCode, parsed.ResponseMessage, Array.Empty<string>());
                break;

            case CFtpStatusCode.Code220Motd:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    Signal(CFtpStatusCode.Code220Motd, string.Empty, _tempAdditionalData.ToArray());
                }
                break;

            case CFtpStatusCode.Code211Feats:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    Signal(CFtpStatusCode.Code211Feats, string.Empty, _tempAdditionalData.ToArray());
                }
                break;

            default:
                // TODO invalid operation
                _messageParsingState = null;
                break;
        }

        void Signal(CFtpStatusCode ftpStatusCode, string responseMessage, string[] additionalData)
        {
            var tempResponse = _waitResponse;
            _waitResponse = new();
            tempResponse.TrySetResult((ftpStatusCode, responseMessage, additionalData));
        }
    }

    public Task<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> WaitResponseAsync()
        => _waitResponse.Task;

    private (CFtpStatusCode FtpStatusCode, bool IsMultiline, string ResponseMessage) ParseFtpLine(string ftpLine)
    {
        var match = _stdParser.Match(ftpLine);
        if (false == match.Success) return (CFtpStatusCode.Code0Undefined, false, string.Empty);
        var ftpStatusCode = match.Groups["statusCode"].Value.ToStatusCode();
        var responseMessage = match.Groups["message"].Value;
        var isMultiline = match.Groups["isMultiline"].Value == "-";
        return (ftpStatusCode, isMultiline, responseMessage);
    }

    public static async Task<bool> ParseListAsync(IAsyncEnumerable<string> reader)
    {
        await using var e = reader.GetAsyncEnumerator();
        await e.MoveNextAsync();
        var status = ParseStatus(e.Current);
        if (status == null) return false;
        return status.Value.Code == (int)CFtpStatusCode.Code125DataAlreadyOpen || status.Value.Code == (int)CFtpStatusCode.Code150OpeningData;
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
            if (status.Value.Code != (int)CFtpStatusCode.Code220Motd) return (false, lines);
            break;
        }
        return (true, lines);
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

    [GeneratedRegex("^(?<statusCode>[0-9]{3}) \"(?<path>.*)\".*$", RegexOptions.Compiled)]
    public static partial Regex CreatePwdRegex();

    [GeneratedRegex("^(?<statusCode>[0-9]{3})(?<isMultiline>.)(?<message>.*)$", RegexOptions.Compiled)]
    private static partial Regex CreateStdStatusRegex();

    [GeneratedRegex("(?:[\\|,])(?<PortNumber>\\d+)(?:[\\|,])")]
    public static partial Regex CreateEpsvPortRegex();

    private enum FeatsParseStates
    {
        Header,
        Body
    }
}
