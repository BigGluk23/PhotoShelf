using System.Security.Cryptography;
using System.Text;
using PhotoShelf.Application.Media;
using PhotoShelf.Domain;
using Xunit;

namespace PhotoShelf.Application.Tests;

public sealed class MediaAdmissionProbeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "photoshelf-admission-" + Guid.NewGuid().ToString("N"));
    private const string SourceText = "export interface Снимок { readonly name: string; }\r\n\t// synthetic source\n";

    public static IEnumerable<object[]> TextCases()
    {
        foreach (var name in new[] { "source.ts", "source.TS", "types.d.ts", "source.mts", "types.d.mts" })
        foreach (var encoding in new[] { "utf8", "utf8-bom", "utf16-le", "utf16-be" })
            yield return [name, encoding];
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void TextSourceCandidatesAreRecognizedWithoutChangingOriginals(string name, string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            _ => new UTF8Encoding(false)
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(SourceText)).ToArray();
        var path = Write(name, bytes);
        var modified = File.GetLastWriteTimeUtc(path);
        Assert.True(MediaFormatRegistry.IsVideo(path)); // cheap candidate classification stays I/O-free
        Assert.True(MediaFormatRegistry.HasAmbiguousVideoExtension(path));
        Assert.Equal(MediaAdmissionKind.NonMediaText, MediaAdmissionProbe.Probe(path));
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Theory]
    [InlineData(188, 0, "recording.ts")]
    [InlineData(192, 4, "recording.mts")]
    [InlineData(204, 7, "recording.d.ts")]
    public void TransportPacketEvidenceTakesPrecedenceOverTheFilename(int stride, int offset, string name)
    {
        var bytes = Enumerable.Repeat((byte)0xff, offset + stride * 6).ToArray();
        for (var packet = 0; packet < 6; packet++)
        {
            var start = offset + packet * stride;
            bytes[start] = 0x47; bytes[start + 1] = 0x1f;
            bytes[start + 2] = 0xff; bytes[start + 3] = 0x10;
        }
        var path = Write(name, bytes);
        Assert.Equal(MediaAdmissionKind.TransportStream, MediaAdmissionProbe.Probe(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    public static IEnumerable<object[]> UncertainCases()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0x47, 0x1f, 0xff, 0x10 }]; // incomplete transport packet
        yield return [new byte[] { 0xff, 0x00, 0x80, 0x81 }];
        yield return [new byte[] { 0xc3, 0x28 }]; // malformed UTF-8
        yield return [new byte[] { 0xff, 0xfe, 0x61 }]; // incomplete UTF-16 at EOF
        yield return [new byte[] { 0x61, 0xe2, 0x82 }]; // incomplete UTF-8 at EOF
        yield return [Encoding.UTF8.GetBytes("source\0binary")];
    }

    [Theory]
    [MemberData(nameof(UncertainCases))]
    public void EmptyBinaryMalformedAndTruncatedDataRemainEligible(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        Assert.Equal(MediaAdmissionKind.Unknown, MediaAdmissionProbe.Probe(stream));
    }

    [Fact]
    public void MissingOrLockedCandidatesRemainEligibleAndOtherExtensionsNeedNoFile()
    {
        Assert.Equal(MediaAdmissionKind.Unknown, MediaAdmissionProbe.Probe(Path.Combine(_directory, "missing.ts")));
        Assert.Equal(MediaAdmissionKind.Unknown, MediaAdmissionProbe.Probe(Path.Combine(_directory, "missing.jpg")));
        var path = Write("locked.ts", Encoding.UTF8.GetBytes(SourceText));
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(MediaAdmissionKind.Unknown, MediaAdmissionProbe.Probe(path));
    }

    [Fact]
    public void ReadsAtMostTheBoundedPrefixEvenWhenStreamReturnsSmallChunksAndLeavesItOpen()
    {
        using var stream = new ChunkedStream(Enumerable.Repeat((byte)'a', 1_000_000).ToArray(), 17);
        Assert.Equal(MediaAdmissionKind.NonMediaText, MediaAdmissionProbe.Probe(stream));
        Assert.Equal(MediaAdmissionProbe.MaximumProbeBytes, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void IncompleteCodePointAtPrefixBoundaryIsAllowedButInvalidTrailingByteIsNot()
    {
        var bytes = Enumerable.Repeat((byte)'a', MediaAdmissionProbe.MaximumProbeBytes + 100).ToArray();
        bytes[MediaAdmissionProbe.MaximumProbeBytes - 2] = 0xe2;
        bytes[MediaAdmissionProbe.MaximumProbeBytes - 1] = 0x82;
        bytes[MediaAdmissionProbe.MaximumProbeBytes] = 0xac;
        using var valid = new MemoryStream(bytes);
        Assert.Equal(MediaAdmissionKind.NonMediaText, MediaAdmissionProbe.Probe(valid));
        bytes[MediaAdmissionProbe.MaximumProbeBytes - 2] = (byte)'a';
        bytes[MediaAdmissionProbe.MaximumProbeBytes - 1] = 0xff;
        using var invalid = new MemoryStream(bytes);
        Assert.Equal(MediaAdmissionKind.Unknown, MediaAdmissionProbe.Probe(invalid));
    }

    [Fact]
    public void CancellationIsObservedBeforeAndBetweenReadsWithoutClosingTheCallerStream()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var untouched = new ChunkedStream(Encoding.UTF8.GetBytes(SourceText), 4);
        Assert.Throws<OperationCanceledException>(() => MediaAdmissionProbe.Probe(untouched, cancelled.Token));
        Assert.Equal(0, untouched.BytesRead);
        using var cancellation = new CancellationTokenSource();
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes(SourceText), 4, cancellation.Cancel);
        Assert.Throws<OperationCanceledException>(() => MediaAdmissionProbe.Probe(stream, cancellation.Token));
        Assert.Equal(4, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name); File.WriteAllBytes(path, bytes); return path;
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize, Action? onRead = null) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer[..Math.Min(buffer.Length, chunkSize)]);
            BytesRead += read; onRead?.Invoke(); return read;
        }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
