using AsyncFriendlyStackTrace;
using CoreFtp.Enum;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CoreFtp.Tests.Integration.FtpClientTests.Directories;

public sealed class When_deleting_directories : TestBase
{
    public When_deleting_directories(ITestOutputHelper outputHelper) : base(outputHelper) { }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_throw_exception_when_folder_nonexistent(FtpEncryption encryption)
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

        string randomDirectoryName = Guid.NewGuid().ToString();
        await sut.LoginAsync();
        await Assert.ThrowsAsync<FtpException>(() => sut.DeleteDirectoryAsync(randomDirectoryName, default));
        await sut.LogOutAsync();
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_delete_directory_when_exists(FtpEncryption encryption)
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
        (await sut.ListDirectoriesAsync()).Any(x => x.Name == randomDirectoryName).Should().BeTrue();
        await sut.DeleteDirectoryAsync(randomDirectoryName, default);
        (await sut.ListDirectoriesAsync()).Any(x => x.Name == randomDirectoryName).Should().BeFalse();
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_recursively_delete_folder(FtpEncryption encryption)
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

        await sut.CreateTestResourceWithNameAsync("penguin.jpg", $"{randomDirectoryName}/1/penguin.jpg");

        await sut.CreateDirectoryAsync($"{randomDirectoryName}/1/1/1", default);
        await sut.CreateDirectoryAsync($"{randomDirectoryName}/1/1/2", default);
        await sut.CreateDirectoryAsync($"{randomDirectoryName}/1/2/2", default);
        await sut.CreateDirectoryAsync($"{randomDirectoryName}/2/2/2", default);

        (await sut.ListDirectoriesAsync()).Any(x => x.Name == randomDirectoryName).Should().BeTrue();
        try
        {
            await sut.DeleteDirectoryAsync(randomDirectoryName, default);
        }
        catch (Exception e)
        {
            throw new Exception(e.ToAsyncString());
        }

            (await sut.ListDirectoriesAsync()).Any(x => x.Name == randomDirectoryName).Should().BeFalse();
    }
}
