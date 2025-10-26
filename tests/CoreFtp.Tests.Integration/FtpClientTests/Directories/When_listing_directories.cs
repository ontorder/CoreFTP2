using CoreFtp.Enum;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CoreFtp.Tests.Integration.FtpClientTests.Directories;

public sealed class When_listing_directories : TestBase
{
    public When_listing_directories(ITestOutputHelper outputHelper) : base(outputHelper) { }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_list_directories_in_root(FtpEncryption encryption)
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

        string randomDirectoryName = $"{Guid.NewGuid()}";

        await sut.LoginAsync();
        await sut.CreateDirectoryAsync(randomDirectoryName, default);
        var directories = await sut.ListDirectoriesAsync();

        directories.Any(x => x.Name == randomDirectoryName).Should().BeTrue();

        await sut.DeleteDirectoryAsync(randomDirectoryName, default);
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_list_directories_in_subdirectory(FtpEncryption encryption)
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
        await sut.ChangeWorkingDirectoryAsync(randomDirectoryNames[0], default);
        var directories = await sut.ListDirectoriesAsync();

        directories.Any(x => x.Name == randomDirectoryNames[1]).Should().BeTrue();

        await sut.ChangeWorkingDirectoryAsync($"/{joinedPath}", default);
        foreach (string directory in randomDirectoryNames.Reverse())
        {
            await sut.ChangeWorkingDirectoryAsync("../", default);
            await sut.DeleteDirectoryAsync(directory, default);
        }
    }
}
