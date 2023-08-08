namespace CoreFtp.Tests.Integration.FtpClientTests.Files;

using System;
using System.Linq;
using System.Threading.Tasks;
using Enum;
using FluentAssertions;
using Helpers;
using Xunit;
using Xunit.Abstractions;
using Infrastructure.Extensions;

public class When_uploading_a_file : TestBase
{
    public When_uploading_a_file(ITestOutputHelper outputHelper) : base(outputHelper) { }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_upload_file(FtpEncryption encryption)
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
        string randomFileName = $"{Guid.NewGuid()}.jpg";

        await sut.LoginAsync();
        var fileinfo = ResourceHelpers.GetResourceFileInfo("penguin.jpg");

        using (var writeStream = await sut.OpenFileWriteStreamAsync(randomFileName, default))
        {
            var fileReadStream = fileinfo.OpenRead();
            await fileReadStream.CopyToAsync(writeStream);
        }

        var files = await sut.ListFilesAsync();

        files.Any(x => x.Name == randomFileName).Should().BeTrue();

        await sut.DeleteFileAsync(randomFileName, default);
        await sut.ChangeWorkingDirectoryAsync("/", default);
        await sut.DeleteDirectoryAsync(sut.Configuration.BaseDirectory, default);
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_upload_file_to_subdirectory(FtpEncryption encryption)
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
        string randomFileName = $"{Guid.NewGuid()}.jpg";

        await sut.LoginAsync();
        await sut.CreateDirectoryAsync(randomDirectoryName, default);
        var fileinfo = ResourceHelpers.GetResourceFileInfo("penguin.jpg");

        using (var writeStream = await sut.OpenFileWriteStreamAsync($"/{randomDirectoryName}/{randomFileName}", default))
        {
            var fileReadStream = fileinfo.OpenRead();
            await fileReadStream.CopyToAsync(writeStream);
        }

        await sut.ChangeWorkingDirectoryAsync(randomDirectoryName, default);

        var files = await sut.ListFilesAsync();
        files.Any(x => x.Name == randomFileName).Should().BeTrue();

        await sut.DeleteFileAsync(randomFileName, default);
        await sut.ChangeWorkingDirectoryAsync("../", default);
        await sut.DeleteDirectoryAsync(randomDirectoryName, default);
    }

    [Theory]
    [InlineData(FtpEncryption.None)]
    [InlineData(FtpEncryption.Explicit)]
    [InlineData(FtpEncryption.Implicit)]
    public async Task Should_upload_file_to_subdirectory_when_given_as_basepath(FtpEncryption encryption)
    {
        var guid = Guid.NewGuid();
        using var sut = new FtpClient(new FtpClientConfiguration
        {
            Host = Program.FtpConfiguration.Host,
            Username = Program.FtpConfiguration.Username,
            Password = Program.FtpConfiguration.Password,
            Port = encryption == FtpEncryption.Implicit
                ? 990
                : Program.FtpConfiguration.Port,
            EncryptionType = encryption,
            IgnoreCertificateErrors = true,
            BaseDirectory = $"{guid}/abc/123/doraeme"
        });
        sut.Logger = Logger;
        string randomFileName = $"{guid}.jpg";

        await sut.LoginAsync();
        var fileinfo = ResourceHelpers.GetResourceFileInfo("penguin.jpg");

        using (var writeStream = await sut.OpenFileWriteStreamAsync($"another_level/{randomFileName}", default))
        {
            var fileReadStream = fileinfo.OpenRead();
            await fileReadStream.CopyToAsync(writeStream);
        }

        await sut.ChangeWorkingDirectoryAsync("/".CombineAsUriWith(sut.Configuration.BaseDirectory)
                                                  .CombineAsUriWith("another_level"), default);

        var files = await sut.ListFilesAsync();
        files.Any(x => x.Name == randomFileName).Should().BeTrue();

        await sut.DeleteDirectoryAsync($"/{guid}", default);
    }
}