namespace PhotoShelf.Application.Updates;

/// <summary>Only public verification material is shipped. Private signing keys never belong in the app.</summary>
public static class UpdateTrust
{
    public const int CurrentCatalogSchema = 5;
    public static string PublicKeyPem { get; } = ReadPublicKey();
    private static string ReadPublicKey()
    {
        using var stream = typeof(UpdateTrust).Assembly.GetManifestResourceStream("PhotoShelf.UpdateSigningPublicKey")
            ?? throw new InvalidOperationException("The update verification key is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
