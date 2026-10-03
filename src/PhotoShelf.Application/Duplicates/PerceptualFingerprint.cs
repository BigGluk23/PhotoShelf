using System.Numerics;

namespace PhotoShelf.Application.Duplicates;

public enum PerceptualFingerprintStatus
{
    Pending,
    Found,
    Unsupported,
    Corrupt,
    TransientError
}

/// <summary>
/// A versioned visual signature. DifferenceHash captures edges while AverageHash captures
/// broad luminance layout. It is a similarity hint, never proof that files are duplicates.
/// </summary>
public sealed record PerceptualFingerprint(
    int AlgorithmVersion,
    ulong DifferenceHash,
    ulong AverageHash,
    int PixelWidth,
    int PixelHeight)
{
    public const int CurrentAlgorithmVersion = 1;
    public const int BandCount = 4;

    public int DifferenceDistance(PerceptualFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (AlgorithmVersion != other.AlgorithmVersion)
            throw new ArgumentException("Fingerprints from different algorithm versions cannot be compared.", nameof(other));
        return BitOperations.PopCount(DifferenceHash ^ other.DifferenceHash);
    }

    public int AverageDistance(PerceptualFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (AlgorithmVersion != other.AlgorithmVersion)
            throw new ArgumentException("Fingerprints from different algorithm versions cannot be compared.", nameof(other));
        return BitOperations.PopCount(AverageHash ^ other.AverageHash);
    }

    public ushort DifferenceBand(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BandCount);
        return (ushort)(DifferenceHash >> (index * 16));
    }
}

public sealed record PerceptualFingerprintReadResult(
    PerceptualFingerprintStatus Status,
    PerceptualFingerprint? Fingerprint = null,
    string? ErrorCode = null,
    DateTime? RetryAtUtc = null)
{
    public static PerceptualFingerprintReadResult Found(PerceptualFingerprint fingerprint) =>
        new(PerceptualFingerprintStatus.Found, fingerprint);

    public static PerceptualFingerprintReadResult Unsupported(string? errorCode = null) =>
        new(PerceptualFingerprintStatus.Unsupported, ErrorCode: errorCode);

    public static PerceptualFingerprintReadResult Corrupt(string? errorCode = null) =>
        new(PerceptualFingerprintStatus.Corrupt, ErrorCode: errorCode);

    public static PerceptualFingerprintReadResult Transient(string? errorCode, DateTime retryAtUtc) =>
        new(PerceptualFingerprintStatus.TransientError, ErrorCode: errorCode,
            RetryAtUtc: retryAtUtc.ToUniversalTime());
}

/// <summary>Pure, platform-independent visual fingerprint calculation over an 8-bit grayscale image.</summary>
public static class PerceptualFingerprintAlgorithm
{
    public static PerceptualFingerprint Compute(ReadOnlySpan<byte> gray, int width, int height, int stride)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, width);
        if (gray.Length < checked(stride * height))
            throw new ArgumentException("The grayscale buffer is smaller than the declared image.", nameof(gray));

        Span<byte> difference = stackalloc byte[9 * 8];
        Span<byte> average = stackalloc byte[8 * 8];
        Resample(gray, width, height, stride, difference, 9, 8);
        Resample(gray, width, height, stride, average, 8, 8);

        ulong differenceHash = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                if (difference[y * 9 + x + 1] > difference[y * 9 + x])
                    differenceHash |= 1UL << (y * 8 + x);

        long sum = 0;
        foreach (var value in average) sum += value;
        var mean = sum / 64d;
        ulong averageHash = 0;
        for (var index = 0; index < average.Length; index++)
            if (average[index] >= mean) averageHash |= 1UL << index;

        return new(PerceptualFingerprint.CurrentAlgorithmVersion, differenceHash, averageHash, width, height);
    }

    private static void Resample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride,
        Span<byte> target, int targetWidth, int targetHeight)
    {
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = ((y + 0.5) * sourceHeight / targetHeight) - 0.5;
            var y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            var y1 = Math.Min(sourceHeight - 1, y0 + 1);
            var fy = Math.Clamp(sourceY - y0, 0, 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = ((x + 0.5) * sourceWidth / targetWidth) - 0.5;
                var x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                var x1 = Math.Min(sourceWidth - 1, x0 + 1);
                var fx = Math.Clamp(sourceX - x0, 0, 1);
                var top = source[y0 * sourceStride + x0] * (1 - fx) + source[y0 * sourceStride + x1] * fx;
                var bottom = source[y1 * sourceStride + x0] * (1 - fx) + source[y1 * sourceStride + x1] * fx;
                target[y * targetWidth + x] = (byte)Math.Clamp((int)Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
            }
        }
    }
}
