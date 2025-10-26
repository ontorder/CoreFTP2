using CoreFtp.Enum;
using CoreFtp.Infrastructure.Extensions;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CoreFtp;

public partial class FtpModelParser
{
    public ChannelReader<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> ResponseReader => _responseChannel.Reader;

    private CFtpStatusCode? _messageParsingState = null;
    private static readonly Regex _stdParser = CreateStdStatusRegex();
    private readonly List<string> _tempAdditionalData = new();
    private readonly Channel<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> _responseChannel = Channel.CreateUnbounded<(CFtpStatusCode Code, string Message, string[]? AdditionalData)>();
    //private TaskCompletionSource<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> _waitResponse = new();

    public async Task ParseAndSignalAsync(string ftpLine)
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
                        await _responseChannel.Writer.WriteAsync((parsed.FtpStatusCode, parsed.ResponseMessage, null));
                        return;
                }

                await _responseChannel.Writer.WriteAsync((parsed.FtpStatusCode, parsed.ResponseMessage, Array.Empty<string>()));
                break;

            case CFtpStatusCode.Code220Motd:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    await _responseChannel.Writer.WriteAsync((CFtpStatusCode.Code220Motd, string.Empty, _tempAdditionalData.ToArray()));
                }
                break;

            case CFtpStatusCode.Code211Feats:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    await _responseChannel.Writer.WriteAsync((CFtpStatusCode.Code211Feats, string.Empty, _tempAdditionalData.ToArray()));
                }
                break;

            default:
                // TODO invalid operation
                _messageParsingState = null;
                break;
        }
    }

    private static (CFtpStatusCode FtpStatusCode, bool IsMultiline, string ResponseMessage) ParseFtpLine(string ftpLine)
    {
        var match = _stdParser.Match(ftpLine);
        if (false == match.Success) return (CFtpStatusCode.Code0Undefined, false, string.Empty);
        var ftpStatusCode = match.Groups["statusCode"].Value.ToStatusCode();
        var responseMessage = match.Groups["message"].Value;
        var isMultiline = match.Groups["isMultiline"].Value == "-";
        return (ftpStatusCode, isMultiline, responseMessage);
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
