using PhotoShelf.Application.Duplicates;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class PerceptualFingerprintTests
{
    [Fact]
    public void HorizontalGradientIsStableAcrossSourceSizes()
    {
        var small = Gradient(18, 16);
        var large = Gradient(180, 120);

        var left = PerceptualFingerprintAlgorithm.Compute(small, 18, 16, 18);
        var right = PerceptualFingerprintAlgorithm.Compute(large, 180, 120, 180);

        Assert.Equal(left.DifferenceHash, right.DifferenceHash);
        Assert.Equal(0, left.DifferenceDistance(right));
        Assert.True(left.AverageDistance(right) <= 2);
    }

    [Fact]
    public void OneChangedBitHasDistanceOneAndOneDifferentBand()
    {
        var left = new PerceptualFingerprint(1, 0, 0, 10, 10);
        var right = new PerceptualFingerprint(1, 1UL << 19, 0, 10, 10);

        Assert.Equal(1, left.DifferenceDistance(right));
        Assert.Single(Enumerable.Range(0, PerceptualFingerprint.BandCount),
            index => left.DifferenceBand(index) != right.DifferenceBand(index));
    }

    [Fact]
    public void InvalidBufferAndMixedVersionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => PerceptualFingerprintAlgorithm.Compute(new byte[3], 2, 2, 2));
        var left = new PerceptualFingerprint(1, 0, 0, 1, 1);
        var right = new PerceptualFingerprint(2, 0, 0, 1, 1);
        Assert.Throws<ArgumentException>(() => left.DifferenceDistance(right));
    }

    private static byte[] Gradient(int width, int height)
    {
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                result[y * width + x] = (byte)Math.Round(x * 255d / Math.Max(1, width - 1));
        return result;
    }
}
