using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using CoreFtp.Infrastructure.Extensions;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CoreFtp;

public partial class FtpModelParser
{
    public ChannelReader<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> ResponseReader => _responseChannel.Reader;

    private CFtpStatusCode? _messageParsingState = null;
    private readonly Channel<(CFtpStatusCode Code, string Message, string[]? AdditionalData)> _responseChannel = Channel.CreateUnbounded<(CFtpStatusCode Code, string Message, string[]? AdditionalData)>();
    private static readonly Regex _stdParser = CreateStdStatusRegex();
    private readonly List<string> _tempAdditionalData = new();

    public async Task ParseAndSignalAsync(string ftpLine, CancellationToken cancellation)
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
                        await _responseChannel.Writer.WriteAsync((parsed.FtpStatusCode, parsed.ResponseMessage, null), cancellation);
                        return;
                }

                await _responseChannel.Writer.WriteAsync((parsed.FtpStatusCode, parsed.ResponseMessage, Array.Empty<string>()), cancellation);
                break;

            case CFtpStatusCode.Code220Motd:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    await _responseChannel.Writer.WriteAsync((CFtpStatusCode.Code220Motd, string.Empty, _tempAdditionalData.ToArray()), cancellation);
                }
                break;

            case CFtpStatusCode.Code211Feats:
                _tempAdditionalData.Add(ftpLine);
                if (parsed.FtpStatusCode == CFtpStatusCode.Code211Feats && parsed.IsMultiline == false)
                {
                    _messageParsingState = null;
                    await _responseChannel.Writer.WriteAsync((CFtpStatusCode.Code211Feats, string.Empty, _tempAdditionalData.ToArray()), cancellation);
                }
                break;

            default:
                // TODO invalid operation
                _messageParsingState = null;
                break;
        }
    }

    // TODO im pretty sure spaces are well defined and parsing shouldn't use trim
    // example: type=file;size=4222572089;modify=20251023213220.665;perms=awr; filename.MP4
    public static FtpNodeInformation ParseFtpNode(string nodeAsString)
    {
        var tokens = nodeAsString.Split(';', 5).ToArray();
        if (tokens.Length != 5) throw new InvalidDataException(nodeAsString);

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
            NodeType = ParseNodeType( nodeType.Trim()),
            Name = filename,
            Size = size.ParseOrDefault(),
            DateModified = dateModified.ParseExactOrDefault("yyyyMMddHHmmss", "yyyyMMddHHmmss.fff")
        };
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

    private static FtpNodeType ParseNodeType(string operand) => operand switch
    {
        "dir" => FtpNodeType.Directory,
        "file" => FtpNodeType.File,
        _ => FtpNodeType.SymbolicLink,
    };

    [GeneratedRegex("(?:[\\|,])(?<PortNumber>\\d+)(?:[\\|,])")]
    public static partial Regex CreateEpsvPortRegex();

    [GeneratedRegex("([0-9]{1,3}[|,]){1,}[0-9]{1,3}")]
    public static partial Regex CreatePasvPortRegex();

    [GeneratedRegex("^\"(?<path>.*)\".*$", RegexOptions.Compiled)]
    public static partial Regex CreatePwdRegex();

    [GeneratedRegex("^(?<statusCode>[0-9]{3})(?<isMultiline>.)(?<message>.*)$", RegexOptions.Compiled)]
    private static partial Regex CreateStdStatusRegex();

    private enum FeatsParseStates
    {
        Header,
        Body
    }
}
