namespace PhotoShelf.Application.Updates.Installation;

public sealed record ActiveInstallationPointer(int ProtocolVersion, string InstallationId, string Version);
public sealed record ActiveInstallationTarget(string ExecutablePath, string InstallationId, string Version);

public static class ActiveInstallationResolver
{
    /// <summary>No network or whole-package hashing at normal startup. Full byte validation precedes pointer activation.</summary>
    public static ActiveInstallationTarget? Resolve(UpdateInstallationPaths paths, string publicKeyPem, int catalogSchema = 5)
    {
        ActiveInstallationPointer pointer;
        try { pointer = UpdateInstallationPaths.ReadJson<ActiveInstallationPointer>(paths.ActivePointerPath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if (pointer.ProtocolVersion != 1) throw new InvalidDataException("Unsupported active installation pointer.");
        var directory = paths.InstallationDirectory(pointer.InstallationId);
        // A corrupt/missing signed manifest does not authorize falling back to an older executable/catalog writer.
        var manifest = UpdateInstallationPaths.ReadBounded(Path.Combine(directory, "photoshelf-update.json"),
            UpdateManifestVerifier.MaximumManifestBytes);
        var signature = UpdateInstallationPaths.ReadBounded(Path.Combine(directory, "photoshelf-update.sig"), 1024);
        var release = UpdateManifestVerifier.Verify(manifest, signature, publicKeyPem, catalogSchema);
        if (release.Version != pointer.Version) throw new InvalidDataException("Active installation identity differs from its signed release.");
        var executable = Path.Combine(directory, "app", "PhotoShelf.exe");
        UpdateInstallationPaths.RejectLinks(executable);
        if ((File.GetAttributes(executable) & FileAttributes.Directory) != 0 || new FileInfo(executable).Length == 0)
            throw new InvalidDataException("The active application executable is missing or empty.");
        return new(executable, pointer.InstallationId, pointer.Version);
    }
}
