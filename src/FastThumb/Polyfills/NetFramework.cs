#if NETFRAMEWORK
// Members of .NET (Core) that .NET Framework lacks and the Polyfill package does not supply. Each one does what its
// .NET counterpart documents, so the readers build unchanged for every target.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System
{
    /// <summary>The single-precision math of .NET Core 2.0, computed in double and rounded back to float.</summary>
    internal static class MathF
    {
        public static float Abs(float x) => Math.Abs(x);
        public static float Floor(float x) => (float)Math.Floor(x);
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        public static float Round(float x) => (float)Math.Round(x);
        public static float Round(float x, int digits) => (float)Math.Round(x, digits);
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
    }
}

namespace System.Collections.Generic
{
    /// <summary>The reference-identity comparer of .NET 5.</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object?>, System.Collections.IEqualityComparer
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        private ReferenceEqualityComparer() { }

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object? obj) => RuntimeHelpers.GetHashCode(obj!);
    }
}

namespace FastThumb
{
    internal static class NetFrameworkPolyfill
    {
        extension(double)
        {
            public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        }

        extension(Array)
        {
            /// <summary>Largest number of elements an array may hold (.NET 6).</summary>
            public static int MaxLength => 0x7FFFFFC7;

            public static void Clear(Array array) => Array.Clear(array, 0, array.Length);

            public static void Fill<T>(T[] array, T value) => array.AsSpan().Fill(value);
        }

        extension(TimeSpan)
        {
            public static TimeSpan operator *(int factor, TimeSpan time) => TimeSpan.FromTicks(factor * time.Ticks);
        }

        extension(BitOperations)
        {
            /// <summary>Integer base-2 logarithm, rounded down; 0 for 0 (.NET Core 3.0).</summary>
            public static int Log2(uint value)
            {
                var log = 0;
                while ((value >>= 1) != 0) log++;
                return log;
            }
        }

        extension(RuntimeHelpers)
        {
            public static bool TryEnsureSufficientExecutionStack()
            {
                try
                {
                    RuntimeHelpers.EnsureSufficientExecutionStack();
                    return true;
                }
                catch (InsufficientExecutionStackException)
                {
                    return false;
                }
            }
        }

        extension(Marshal)
        {
            /// <summary>A NUL-terminated UTF-8 string from native memory (.NET Core 1.1); null for a null pointer.</summary>
            public static unsafe string? PtrToStringUTF8(IntPtr ptr)
            {
                if (ptr == IntPtr.Zero) return null;
                var bytes = (byte*)ptr;
                var length = 0;
                while (bytes[length] != 0) length++;
                return Encoding.UTF8.GetString(bytes, length);
            }
        }
    }
}
#endif
