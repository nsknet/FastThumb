#if NETFRAMEWORK
// What the tests use of .NET (Core) and neither .NET Framework nor the Polyfill package has.
using System.Text;

namespace System.Runtime.CompilerServices
{
    /// <summary>The compiler turns array[a..b] into a call of this .NET Core 3.0 member.</summary>
    internal static class RuntimeHelpers
    {
        public static T[] GetSubArray<T>(T[] array, Range range)
        {
            var (offset, length) = range.GetOffsetAndLength(array.Length);
            return array.AsSpan(offset, length).ToArray();
        }
    }
}

namespace FastThumb.Tests
{
    internal static class NetFrameworkPolyfills
    {
        public static string Replace(this string text, string oldValue, string newValue, StringComparison comparison) =>
            comparison == StringComparison.Ordinal ? text.Replace(oldValue, newValue) : throw new NotSupportedException();

        public static StringBuilder Append(this StringBuilder builder, IFormatProvider provider, FormattableString text) =>
            builder.Append(text.ToString(provider));

        extension(TimeSpan)
        {
            public static TimeSpan operator /(TimeSpan time, int divisor) => TimeSpan.FromTicks(time.Ticks / divisor);
            public static TimeSpan operator *(TimeSpan time, double factor) => TimeSpan.FromTicks((long)Math.Round(time.Ticks * factor));
        }
    }
}
#endif
