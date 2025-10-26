using System.Collections.ObjectModel;
using CoreFtp.Enum;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CoreFtp.Tests.Integration.FtpClientTests.Directories;

public sealed class When_creating_directories : TestBase
{
    public When_creating_directories(ITestOutputHelper outputHelper) : base(outputHelper) { }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_create_a_directory(FtpEncryption encryption)
    {
        string randomDirectoryName = Guid.NewGuid().ToString();
        ReadOnlyCollection<FtpNodeInformation> directories;

        using (var sut = new FtpClient(new FtpClientConfiguration
        {
            Host = Program.FtpConfiguration.Host,
            Username = Program.FtpConfiguration.Username,
            Password = Program.FtpConfiguration.Password,
            Port = encryption == FtpEncryption.Implicit
                ? 990
                : Program.FtpConfiguration.Port,
            EncryptionType = encryption,
            IgnoreCertificateErrors = true
        }))
        {
            sut.Logger = Logger;
            await sut.LoginAsync();
            await sut.CreateDirectoryAsync(randomDirectoryName, default);
            directories = await sut.ListDirectoriesAsync();
            await sut.DeleteDirectoryAsync(randomDirectoryName, default);
            await sut.LogOutAsync();
        }

        directories.Any(x => x.Name == randomDirectoryName).Should().BeTrue();
    }
}
