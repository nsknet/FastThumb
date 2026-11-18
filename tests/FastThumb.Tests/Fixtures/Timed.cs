namespace FastThumb.Tests.Fixtures;

/// <summary>
/// Tests that compare one run's duration with another's. xunit runs this collection on its own, after the parallel ones,
/// so the other tests neither load the machine nor hold a shared lock (PDFium) while it measures.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Timed
{
    public const string Name = "Timed";
}
