using System;
using System.Threading.Tasks;
using CoreFtp.Enum;
using CoreFtp.Infrastructure;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CoreFtp.Tests.Integration.FtpClientTests.Directories;

public class When_changing_working_directories : TestBase
{
    public When_changing_working_directories(ITestOutputHelper outputHelper) : base(outputHelper) { }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_fail_when_changing_to_a_nonexistent_directory(FtpEncryption encryption)
    {
        using var sut = new FtpClient(new FtpClientConfiguration
        {
            Host = Program.FtpConfiguration.Host,
            Username = Program.FtpConfiguration.Username,
            Password = Program.FtpConfiguration.Password,
            Port = encryption == FtpEncryption.Implicit
                ? 990
                : Program.FtpConfiguration.Port,
            EncryptionType = encryption,
            IgnoreCertificateErrors = true
        });
        sut.Logger = Logger;
        await sut.LoginAsync();
        await sut.SetClientNameAsync(nameof(Should_fail_when_changing_to_a_nonexistent_directory));
        await Assert.ThrowsAsync<FtpException>(() => sut.ChangeWorkingDirectoryAsync(Guid.NewGuid().ToString(), default));
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_change_to_directory_when_exists(FtpEncryption encryption)
    {
        string randomDirectoryName = Guid.NewGuid().ToString();

        using var sut = new FtpClient(new FtpClientConfiguration
        {
            Host = Program.FtpConfiguration.Host,
            Username = Program.FtpConfiguration.Username,
            Password = Program.FtpConfiguration.Password,
            Port = encryption == FtpEncryption.Implicit
                ? 990
                : Program.FtpConfiguration.Port,
            EncryptionType = encryption,
            IgnoreCertificateErrors = true
        });
        sut.Logger = Logger;
        await sut.LoginAsync();
        await sut.CreateDirectoryAsync(randomDirectoryName, default);
        await sut.ChangeWorkingDirectoryAsync(randomDirectoryName, default);
        sut.WorkingDirectory.Should().Be($"/{randomDirectoryName}");

        await sut.ChangeWorkingDirectoryAsync("../", default);
        await sut.DeleteDirectoryAsync(randomDirectoryName, default);
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_change_to_deep_directory_when_exists(FtpEncryption encryption)
    {
        string[] randomDirectoryNames =
        {
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString()
        };
        using var sut = new FtpClient(new FtpClientConfiguration
        {
            Host = Program.FtpConfiguration.Host,
            Username = Program.FtpConfiguration.Username,
            Password = Program.FtpConfiguration.Password,
            Port = encryption == FtpEncryption.Implicit
                ? 990
                : Program.FtpConfiguration.Port,
            EncryptionType = encryption,
            IgnoreCertificateErrors = true
        });
        sut.Logger = Logger;
        string joinedPath = string.Join("/", randomDirectoryNames);
        await sut.LoginAsync();

        await sut.CreateDirectoryAsync(joinedPath, default);
        await sut.ChangeWorkingDirectoryAsync(joinedPath, default);
        sut.WorkingDirectory.Should().Be($"/{joinedPath}");

        foreach (string directory in randomDirectoryNames.Reverse())
        {
            await sut.ChangeWorkingDirectoryAsync("../", default);
            await sut.DeleteDirectoryAsync(directory, default);
        }
    }
}
