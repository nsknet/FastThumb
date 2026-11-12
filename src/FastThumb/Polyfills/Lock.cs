#if !NET9_0_OR_GREATER
namespace System.Threading;

/// <summary>
/// Stand-in for the .NET 9 lock type on older runtimes. The compiler turns <c>lock (x)</c> on a value of this type into
/// <see cref="EnterScope"/>, so the same source builds for every target.
/// </summary>
internal sealed class Lock
{
    private readonly object _monitor = new();

    public Scope EnterScope()
    {
        Monitor.Enter(_monitor);
        return new Scope(_monitor);
    }

    public readonly ref struct Scope(object monitor)
    {
        public void Dispose() => Monitor.Exit(monitor);
    }
}
#endif
