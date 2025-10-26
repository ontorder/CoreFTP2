﻿using System.IO;

namespace CoreFtp.Tests.Integration.Helpers;

using Enum;

public static class ResourceHelpers
{
    public static DirectoryInfo GetResourceDirectoryInfo(string directory = "")
    {
        return new DirectoryInfo($"{AppContext.BaseDirectory}/Resources/{directory}");
    }

    public static FileInfo GetResourceFileInfo(string filename)
    {
        return new FileInfo($"{AppContext.BaseDirectory}/Resources/{filename}");
    }

    public static async Task CreateTestResourceWithNameAsync(this FtpClient ftpClient, string resourceName, string asFileName)
    {
        var resourceFileInfo = GetResourceFileInfo(resourceName);
        await ftpClient.SetTransferMode(FtpTransferMode.Binary);
        using var writeStream = await ftpClient.OpenFileWriteStreamAsync(asFileName, default);
        var fileReadStream = resourceFileInfo.OpenRead();
        await fileReadStream.CopyToAsync(writeStream);
    }

    public static string GetTempFilePath()
    {
        return Path.GetTempPath();
    }

    public static FileInfo GetTempFileInfo()
    {
        return new FileInfo(Path.GetTempFileName());
    }
}
