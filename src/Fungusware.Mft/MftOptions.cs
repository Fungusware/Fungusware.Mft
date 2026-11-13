namespace Fungusware.Mft;

/// <summary>Which size of a file counts.</summary>
public enum MftMeasure
{
    /// <summary>Space the file takes on the volume (allocated clusters; the compressed size for a compressed or sparse file). This is what "where did the space go" wants. Files small enough to live inside their MFT record count as 0.</summary>
    OnDisk,

    /// <summary>The length of the file's data, as Explorer shows under "Size".</summary>
    Length,
}

/// <summary>What <see cref="MftReader"/> keeps from the volume.</summary>
public sealed class MftOptions
{
    /// <summary>Which size of a file counts. Default: <see cref="MftMeasure.OnDisk"/>.</summary>
    public MftMeasure Measure { get; init; } = MftMeasure.OnDisk;

    /// <summary>
    /// Files smaller than this keep no name (a volume can hold millions of files, and a name costs far more memory than a size).
    /// Their sizes still count towards every folder. <see cref="MftVolume.LargestFiles"/> can only list files at least this big. Default: 1 MiB.
    /// </summary>
    public long KeepFileNamesAtLeast { get; init; } = 1L << 20;
}
