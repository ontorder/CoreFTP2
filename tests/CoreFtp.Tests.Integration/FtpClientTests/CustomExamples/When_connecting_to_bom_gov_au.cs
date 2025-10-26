﻿using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace CoreFtp.Tests.Integration.FtpClientTests.CustomExamples;

public class When_connecting_to_bom_gov_au : TestBase
{
    public When_connecting_to_bom_gov_au( ITestOutputHelper outputHelper ) : base( outputHelper ) { }

    [ Fact( Skip = "Example") ]
    public async Task Should_give_directory_listing()
    {
        using ( var sut = new FtpClient( new FtpClientConfiguration
        {
            Host = "ftp.bom.gov.au",
            Username = "anonymous",
            Password = "guest",
            Port = 21,
            EncryptionType = FtpEncryption.None,
            IgnoreCertificateErrors = true,
            BaseDirectory = "/anon/gen/fwo/"
        } ) )
        {
            sut.Logger = Logger;
            await sut.LoginAsync();
            var directoryListing = await sut.ListAllAsync();

            directoryListing.Count.Should().BeGreaterThan( 0 );
        }
    }
}
