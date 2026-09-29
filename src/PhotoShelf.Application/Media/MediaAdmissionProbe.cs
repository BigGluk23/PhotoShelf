using System.Text;
using PhotoShelf.Domain;

namespace PhotoShelf.Application.Media;

public enum MediaAdmissionKind { Unknown, TransportStream, NonMediaText }

/// <summary>
/// Worker-only, bounded admission check for extensions shared by source code and video.
/// Unknown data remains eligible. This never reclassifies or removes a catalog record.
/// </summary>
public static class MediaAdmissionProbe
{
    public const int MaximumProbeBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Little = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Big = new(true, false, true);

    public static MediaAdmissionKind Probe(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!MediaFormatRegistry.HasAmbiguousVideoExtension(path)) return MediaAdmissionKind.Unknown;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Probe(stream, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed read is not evidence that a media file is source code.
            return MediaAdmissionKind.Unknown;
        }
    }

    /// <summary>Reads at most MaximumProbeBytes from the current position; leaves the stream open.</summary>
    public static MediaAdmissionKind Probe(Stream stream, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> prefix = stackalloc byte[MaximumProbeBytes];
        var length = 0;
        var complete = false;
        while (length < prefix.Length)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(prefix[length..]);
            if (read == 0) { complete = true; break; }
            length += read;
        }
        token.ThrowIfCancellationRequested();
        var bytes = prefix[..length];
        if (HasTransportPackets(bytes)) return MediaAdmissionKind.TransportStream;
        // Empty, binary, malformed and uncertain content remains eligible. Text is
        // rejected only on the new-record admission path, never during catalog repair.
        return LooksLikeText(bytes, complete) ? MediaAdmissionKind.NonMediaText : MediaAdmissionKind.Unknown;
    }

    private static bool HasTransportPackets(ReadOnlySpan<byte> bytes)
    {
        const int packets = 5;
        foreach (var stride in new[] { 188, 192, 204 })
        {
            var maximumStart = bytes.Length - ((packets - 1) * stride + 4);
            var start = 0;
            while (start <= maximumStart)
            {
                var next = bytes.Slice(start, maximumStart - start + 1).IndexOf((byte)0x47);
                if (next < 0) break;
                start += next;
                var valid = true;
                for (var packet = 0; packet < packets; packet++)
                {
                    var offset = start + packet * stride;
                    // A sync byte alone is insufficient. Require a legal nonreserved
                    // adaptation/payload mode and no transport-error flag as well.
                    if (bytes[offset] != 0x47 || (bytes[offset + 1] & 0x80) != 0 || (bytes[offset + 3] & 0x30) == 0)
                    { valid = false; break; }
                }
                if (valid) return true;
                start++;
            }
        }
        return false;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes, bool complete)
    {
        if (bytes.Length == 0) return false;
        Encoding encoding = StrictUtf8;
        if (bytes.StartsWith(new byte[] { 0xff, 0xfe })) { encoding = StrictUtf16Little; bytes = bytes[2..]; }
        else if (bytes.StartsWith(new byte[] { 0xfe, 0xff })) { encoding = StrictUtf16Big; bytes = bytes[2..]; }
        else if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        // A bounded prefix may end within a multibyte code point. Permit only that
        // final incomplete code point; invalid bytes elsewhere keep the Unknown result.
        try
        {
            Span<char> characters = stackalloc char[MaximumProbeBytes];
            encoding.GetDecoder().Convert(bytes, characters, flush: complete,
                out var bytesUsed, out var charactersUsed, out _);
            if (bytesUsed != bytes.Length || charactersUsed == 0) return false;
            foreach (var character in characters[..charactersUsed])
            {
                if (char.IsControl(character) && character is not ('\r' or '\n' or '\t')) return false;
            }
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}
