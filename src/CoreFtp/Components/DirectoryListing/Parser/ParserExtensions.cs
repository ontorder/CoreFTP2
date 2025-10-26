﻿using System.Globalization;

namespace CoreFtp.Components.DirectoryListing.Parser;

public static class ParserExtensions
{
    public static DateTime ExtractFtpDate( this string date, DateTimeStyles style )
    {
        var formats = new[]
        {
            "yyyyMMddHHmmss",
            "yyyyMMddHHmmss.fff",
            "MMM dd  yyyy",
            "MMM dd yyyy",
            "MMM  d  yyyy",
            "MMM dd HH:mm",
            "MMM  d HH:mm",
            "MMM dd  H:mm",
            "MMM  d  H:mm",
            "MM-dd-yy  hh:mmtt",
            "MM-dd-yyyy  hh:mmtt"
        };

        return DateTime.TryParseExact(date, formats, CultureInfo.InvariantCulture, style, out DateTime parsed)
            ? parsed
            : DateTime.MinValue;
    }
}
