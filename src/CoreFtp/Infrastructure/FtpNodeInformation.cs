using System;
using CoreFtp.Enum;

namespace CoreFtp.Infrastructure;

public sealed class FtpNodeInformation
{
    public DateTime DateModified { get; set; }
    public string Name { get; set; }
    public FtpNodeType NodeType { get; set; }
    public long Size { get; set; }
}
