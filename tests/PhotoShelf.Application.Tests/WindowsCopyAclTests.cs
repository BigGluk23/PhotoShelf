using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PhotoShelf.Application.Files;
using Xunit;

namespace PhotoShelf.Application.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCopyAclTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "photoshelf-acl-test-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source", "private.jpg");
    private string Destination => Path.Combine(_root, "public", "private.jpg");
    private string Journal => Path.Combine(_root, "operations", "move.jsonl");
    private static readonly AccessControlSections Sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    private string Prepare(bool inheritedSource = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        var user = WindowsIdentity.GetCurrent().User!;
        if (inheritedSource)
        {
            var parentAcl = new DirectorySecurity(); parentAcl.SetAccessRuleProtection(true, false);
            parentAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Path.GetDirectoryName(Source)!).SetAccessControl(parentAcl);
        }
        File.WriteAllText(Source, "private original bytes");
        if (!inheritedSource)
        {
            var sourceAcl = new FileSecurity(); sourceAcl.SetAccessRuleProtection(true, false);
            sourceAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(Source).SetAccessControl(sourceAcl);
        }
        var destinationAcl = new DirectoryInfo(Path.GetDirectoryName(Destination)!).GetAccessControl();
        destinationAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Path.GetDirectoryName(Destination)!).SetAccessControl(destinationAcl);
        return Security(Source, freezeInherited: inheritedSource);
    }
    private static string Security(string path, bool freezeInherited = false)
    {
        var descriptor = new RawSecurityDescriptor(new FileInfo(path).GetAccessControl(Sections).GetSecurityDescriptorBinaryForm(), 0);
        // AI/AR describe automatic inheritance bookkeeping, not an access grant. Windows can
        // change these on rename. Keep owner, group, every ACE/order and the protected bit.
        var flags = descriptor.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired);
        if (freezeInherited)
        {
            flags |= ControlFlags.DiscretionaryAclProtected;
            foreach (GenericAce ace in descriptor.DiscretionaryAcl!) ace.AceFlags &= ~AceFlags.Inherited;
        }
        descriptor.SetFlags(flags);
        return descriptor.GetSddlForm(Sections);
    }
    private IReadOnlyList<MoveEntry> Plan(FileMoveService service) => service.Plan([new(Source, null)], Path.GetDirectoryName(Destination)!, CollisionPolicy.Skip);

    [WindowsFact]
    public async Task CopyIntoPublicFolderHasSourceAclBeforeFirstByteAndAfterUndo()
    {
        var expected = Prepare(); var created = false;
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true, Checkpoint = (phase, state) =>
        {
            if (phase == "copy_created")
            {
                created = true;
                Assert.Equal(0, new FileInfo(state.Temporary!).Length);
                Assert.Equal(expected, Security(state.Temporary!));
                Assert.NotNull(state.WindowsSecurityDescriptor);
                Assert.Contains("WindowsSecurityDescriptor", File.ReadAllText(Journal));
            }
            return Task.CompletedTask;
        }});
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.True(result.Moved, result.Error); Assert.True(created); Assert.False(File.Exists(Source));
        Assert.Equal(expected, Security(Destination));
        var undo = Assert.Single(await new FileMoveService(new FileMoveOptions { AlwaysCopy = true }).UndoAsync(Journal,
            Path.Combine(_root, "operations", "undo.jsonl"), _ => Task.CompletedTask, null, default));
        Assert.True(undo.Moved, undo.Error); Assert.Equal(expected, Security(Source));
        Assert.Equal("private original bytes", File.ReadAllText(Source));
    }

    [WindowsFact]
    public async Task ChangedDestinationAclPreventsOriginalCleanup()
    {
        Prepare();
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true, Checkpoint = (phase, state) =>
        {
            if (phase == "before_source_cleanup")
            {
                var acl = new FileInfo(state.Destination).GetAccessControl();
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
                new FileInfo(state.Destination).SetAccessControl(acl);
            }
            return Task.CompletedTask;
        }});
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.False(result.Moved); Assert.Contains("права", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("private original bytes", File.ReadAllText(Source));
        Assert.Equal("private original bytes", File.ReadAllText(Destination));
    }

    [WindowsFact]
    public async Task InheritedSourceRightsBecomeExplicitProtectedRightsBeforeCopyBytes()
    {
        var expected = Prepare(inheritedSource: true);
        var sourceAcl = new FileInfo(Source).GetAccessControl(Sections);
        Assert.False(sourceAcl.AreAccessRulesProtected);
        Assert.Contains(sourceAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => rule.IsInherited);
        var created = false;
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true, Checkpoint = (phase, state) =>
        {
            if (phase == "copy_created")
            {
                created = true; Assert.Equal(0, new FileInfo(state.Temporary!).Length);
                Assert.Equal(expected, Security(state.Temporary!));
                Assert.True(new FileInfo(state.Temporary!).GetAccessControl(Sections).AreAccessRulesProtected);
            }
            return Task.CompletedTask;
        }});
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.True(result.Moved, result.Error); Assert.True(created); Assert.False(File.Exists(Source));
        var targetAcl = new FileInfo(Destination).GetAccessControl(Sections);
        Assert.True(targetAcl.AreAccessRulesProtected);
        Assert.All(targetAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => Assert.False(rule.IsInherited));
        Assert.Equal(expected, Security(Destination)); Assert.Equal("private original bytes", File.ReadAllText(Destination));
    }

    [WindowsFact]
    public async Task RecoveryRetainsDurableSourceAclSnapshot()
    {
        var expected = Prepare();
        var interrupted = new FileMoveService(new FileMoveOptions { AlwaysCopy = true,
            Checkpoint = (phase, _) => phase == "copy_verified" ? throw new MoveInterruptionException("crash before publication") : Task.CompletedTask });
        await Assert.ThrowsAsync<MoveInterruptionException>(() => interrupted.ExecuteAsync(Plan(interrupted), Journal, _ => Task.CompletedTask, null, default));
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true });
        var recovered = Assert.Single(await service.RecoverAsync(Journal, _ => Task.CompletedTask, null, default));
        Assert.True(recovered.Moved, recovered.Error); Assert.False(File.Exists(Source));
        Assert.Equal(expected, Security(Destination)); Assert.Equal("private original bytes", File.ReadAllText(Destination));
    }

    [WindowsFact]
    public async Task SameVolumeRenameKeepsOriginalSecurityWithoutCopyPolicy()
    {
        var expected = Prepare();
        var service = new FileMoveService(new FileMoveOptions { Checkpoint = (phase, _) =>
            phase == "copying" ? throw new Exception("same-volume rename must remain unchanged") : Task.CompletedTask });
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => Task.CompletedTask, null, default));
        Assert.True(result.Moved, result.Error); Assert.Equal(expected, Security(Destination));
    }

    [WindowsFact]
    public async Task MandatoryIntegrityLabelBlocksCopyBeforeAnyTargetBytesExist()
    {
        Prepare();
        // A medium label can be set by its owner without elevating the runner. Failure to
        // create this actual native fixture is a test failure, never an untested green pass.
        Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NRNW;;;ME)", 1, out var descriptor, out _));
        try
        {
            Assert.True(GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _)); Assert.True(present);
            Assert.Equal(0u, SetNamedSecurityInfoW(Source, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl));
        }
        finally { LocalFree(descriptor); }
        var created = false;
        var service = new FileMoveService(new FileMoveOptions { AlwaysCopy = true, Checkpoint = (phase, _) =>
        {
            if (phase == "copy_created") created = true;
            return Task.CompletedTask;
        }});
        var result = Assert.Single(await service.ExecuteAsync(Plan(service), Journal, _ => throw new Exception("must not commit"), null, default));
        Assert.False(result.Moved); Assert.Contains("метка целостности", result.Error!); Assert.False(created);
        Assert.Equal("private original bytes", File.ReadAllText(Source)); Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(_root, "*.photoshelf-copy-*", SearchOption.AllDirectories));
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint SetNamedSecurityInfoW(string path, uint type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
