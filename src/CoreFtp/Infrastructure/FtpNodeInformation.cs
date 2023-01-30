using System;
using CoreFtp.Enum;

namespace CoreFtp.Infrastructure
{
    public class FtpNodeInformation
    {
        public string Name { get; set; }
        public long Size { get; set; }
        public DateTime DateModified { get; set; }
        public FtpNodeType NodeType { get; set; }
    }
}
