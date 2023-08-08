using System;

namespace CoreFtp.Infrastructure;

public sealed class FtpException : Exception
{
    public FtpException() { }

    public FtpException(string message) : base(message) { }

    public FtpException(string message, Exception innerException) : base(message, innerException) { }
}
