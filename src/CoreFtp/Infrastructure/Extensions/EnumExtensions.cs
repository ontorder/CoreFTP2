namespace CoreFtp.Infrastructure.Extensions;

public static class EnumExtensions
{
    public static TEnum? ToNullableEnum<TEnum>(this string operand) where TEnum : struct, IComparable, IFormattable, IConvertible
    {
        if (System.Enum.TryParse(operand, true, out TEnum enumOut))
        {
            return enumOut;
        }

        return null;
    }

    public static TEnum? ToNullableEnum<TEnum>(this int operand) where TEnum : struct, IComparable, IFormattable, IConvertible
    {
        if (System.Enum.IsDefined(typeof(TEnum), operand))
        {
            return (TEnum)(object)operand;
        }

        return null;
    }
}
