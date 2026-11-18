#if NETFRAMEWORK
namespace System.Runtime.InteropServices;

/// <summary>
/// The C <c>unsigned long</c> of .NET 6, for .NET Framework. .NET Framework only runs on Windows, where the type is 32 bits
/// wide in every process, so the stand-in is a blittable wrapper of <see cref="uint"/>.
/// </summary>
internal readonly struct CULong(nuint value)
{
    private readonly uint _value = (uint)value;

    public nuint Value => _value;
}
#endif
