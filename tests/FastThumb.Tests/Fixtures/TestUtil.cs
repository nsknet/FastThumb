using System.Diagnostics;

namespace FastThumb.Tests.Fixtures;

internal static class TestUtil
{
    /// <summary>
    /// Runs <paramref name="action"/> on its own thread and fails the test if it takes longer than <paramref name="limit"/>.
    /// Returns the exception it threw, or null.
    /// </summary>
    public static Exception? Within(TimeSpan limit, Action action)
    {
        var task = Task.Factory.StartNew(() =>
        {
            try { action(); return null; }
            catch (Exception e) { return e; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(task.Wait(limit), $"did not finish within {limit.TotalSeconds:0.#} s");
        return task.Result;
    }

    /// <summary>Like <see cref="Within"/>, and asserts the exception is <typeparamref name="T"/> or derived from it.</summary>
    public static T Throws<T>(TimeSpan limit, Action action) where T : Exception
    {
        var error = Within(limit, action);
        Assert.NotNull(error);
        return Assert.IsAssignableFrom<T>(error);
    }

    public static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    public static TimeSpan Time(Action action)
    {
        var clock = Stopwatch.StartNew();
        action();
        return clock.Elapsed;
    }
}

/// <summary>A read-only stream that cannot seek and reports no length, like a network stream.</summary>
internal sealed class NonSeekableStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1000));   // short reads on purpose
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
