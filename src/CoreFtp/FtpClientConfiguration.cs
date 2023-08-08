using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace CoreFtp;

public sealed class FtpClientConfiguration
{
    public string BaseDirectory { get; set; } = "/";
    public X509CertificateCollection ClientCertificates { get; set; } = new X509CertificateCollection();
    public int? DisconnectTimeoutMilliseconds { get; set; } = 100;
    public FtpEncryption EncryptionType { get; set; } = FtpEncryption.None;
    public string Host { get; set; }
    public bool IgnoreCertificateErrors { get; set; } = true;
    public IpVersion IpVersion { get; set; } = IpVersion.IpV4;
    public FtpTransferMode Mode { get; set; } = FtpTransferMode.Binary;
    public char ModeSecondType { get; set; } = '\0';
    public string Password { get; set; }
    public int Port { get; set; } = Constants.FtpPort;
    public bool ShouldEncrypt => EncryptionType == FtpEncryption.Explicit ||
                                 EncryptionType == FtpEncryption.Implicit &&
                                 Port == Constants.FtpsPort;

    public SslProtocols SslProtocols { get; set; } = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12;
    public int TimeoutSeconds { get; set; } = 120;
    public string Username { get; set; }
}
