using System;
using System.Collections.Generic;
using System.Globalization;

namespace CoreFtp.Infrastructure.Extensions;

public static class DefaultValueExtensions
{
    public static DateTime ParseExactOrDefault(this string operand, params string[] formats)
        => DateTime.TryParseExact(operand, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsedDate)
            ? parsedDate
            : default;

    public static long ParseOrDefault(this string operand) => long.TryParse(operand, out long parsedLong)
            ? parsedLong
            : default;

    public static TVal GetValueOrDefault<TKey, TVal>(this Dictionary<TKey, TVal> operand, TKey key)
    {
        operand.TryGetValue(key, out TVal value);
        return value;
    }
}
