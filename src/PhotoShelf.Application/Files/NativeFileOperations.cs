using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace PhotoShelf.Application.Files;

/// <summary>Byte-copy policy: preserve owner/group and freeze the source DACL before creating any media bytes.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileSecurity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Create(string path, uint access, uint sharing, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle handle, IntPtr volumeName, uint volumeNameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, IntPtr fileSystemName, uint fileSystemNameSize);

    public static string Capture(SafeFileHandle source) => Convert.ToBase64String(Freeze(Read(source)));

    public static void Verify(SafeFileHandle handle, string? expected, bool requireProtected)
    {
        if (expected is null) throw new IOException("В журнале нет прав исходного файла; требуется ручное согласование, оригинал сохранён");
        var actual = Read(handle);
        if (requireProtected && (actual.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            throw new IOException("Целевая копия наследует посторонние права; завершение переноса остановлено, оригинал сохранён");
        var bytes = Parse(expected);
        if (!Freeze(actual).AsSpan().SequenceEqual(bytes))
            throw new IOException("Владелец или права доступа файла отличаются от проверенного источника; оригинал сохранён");
    }

    public static FileStream CreateRestrictedCopy(string path, string descriptor)
    {
        // Check the parent before creating even an empty temporary file. Recheck the actual
        // created handle before writing, so a filesystem that ignores ACLs cannot expose bytes.
        using var parent = OpenDirectory(Path.GetDirectoryName(path)!, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (parent.IsInvalid) throw Error("Не удалось проверить файловую систему назначения");
        RequirePersistentAcl(parent);
        var bytes = Parse(descriptor);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            var security = new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = memory, InheritHandle = false };
            var handle = Create(path, 0xC0000000, 0, ref security, 1, 0xC8000080, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Error("Не удалось создать копию с исходными правами и владельцем"); handle.Dispose(); throw error; }
            try
            {
                RequirePersistentAcl(handle);
                Verify(handle, descriptor, requireProtected: true);
                return new FileStream(handle, FileAccess.Write, 131072, isAsync: true);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static byte[] Parse(string descriptor)
    {
        try
        {
            if (descriptor.Length > 90_000) throw new FormatException("Oversized descriptor");
            var bytes = Convert.FromBase64String(descriptor);
            if (bytes.Length is < 20 or > 65_536) throw new FormatException("Invalid descriptor size");
            var parsed = new RawSecurityDescriptor(bytes, 0);
            if ((parsed.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || !Freeze(parsed).AsSpan().SequenceEqual(bytes))
                throw new FormatException("Descriptor is not a protected source snapshot");
            return bytes;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        { throw new IOException("Журнал содержит некорректные права доступа; автоматическое восстановление остановлено", exception); }
    }

    private static RawSecurityDescriptor Read(SafeFileHandle handle)
    {
        // Owner/group/DACL plus mandatory label, resource attributes and central policy.
        // These restrictions are queryable with READ_CONTROL, unlike audit-only SACL entries.
        // Until we can preserve them at CREATE_NEW, copying must refuse them, not drop them.
        var error = GetSecurityInfo(handle, 1, 0x77, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new IOException($"Не удалось проверить владельца, DACL и дополнительные ограничения доступа (код ОС {error}); оригинал сохранён");
        try
        {
            if (descriptor == IntPtr.Zero) throw new IOException("ОС не вернула права доступа; оригинал сохранён");
            var length = checked((int)GetSecurityDescriptorLength(descriptor));
            if (length is < 20 or > 65_536) throw new IOException("Неподдерживаемый размер прав доступа; оригинал сохранён");
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    private static byte[] Freeze(RawSecurityDescriptor source)
    {
        if (source.Owner is null || source.Group is null || (source.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw new IOException("Нельзя однозначно сохранить владельца и DACL; оригинал сохранён");
        if (source.SystemAcl is { Count: > 0 })
            throw new IOException("У файла есть метка целостности или дополнительная политика доступа Windows; перенос копированием пока не поддержан, оригинал сохранён");
        var acl = source.DiscretionaryAcl;
        if (acl is not null)
            foreach (GenericAce ace in acl) ace.AceFlags &= ~AceFlags.Inherited;
        // Keep every effective ACE and its order. Freeze inherited rules as explicit rules:
        // the destination parent's inheritable permissions must not broaden the source ACL.
        var frozen = new RawSecurityDescriptor(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            source.Owner, source.Group, null, acl);
        var bytes = new byte[frozen.BinaryLength]; frozen.GetBinaryForm(bytes, 0); return bytes;
    }

    private static void RequirePersistentAcl(SafeFileHandle handle)
    {
        if (!GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, out _, out _, out var flags, IntPtr.Zero, 0))
            throw Error("Не удалось проверить поддержку прав доступа томом назначения");
        if ((flags & 8) == 0) throw new IOException("Том назначения не сохраняет ACL (например FAT/exFAT); безопасный перенос между томами остановлен, оригинал сохранён");
    }
    private static IOException Error(string message) => new($"{message} (код ОС {Marshal.GetLastPInvokeError()}); оригинал сохранён");
}

internal static class WindowsFileRemoval
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Create(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref Disposition disposition, uint size);
    public static SafeFileHandle Open(string path)
    {
        var handle = Create(path, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3, 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError(); handle.Dispose();
            throw new IOException($"Не удалось заблокировать оригинал для завершения переноса (код ОС {error}); копии сохранены");
        }
        return handle;
    }
    public static void MarkForDeletion(SafeFileHandle handle)
    {
        var disposition = new Disposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<Disposition>()))
            throw new IOException($"Не удалось завершить перенос (код ОС {Marshal.GetLastPInvokeError()}); копии сохранены");
    }
}

internal static class NativeRename
{
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] private static extern int Sync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
    public static void MoveExclusive(string source, string destination)
    {
        if (!TryMove(source, destination)) throw new IOException("Атомарное переименование недоступно; файлы сохранены");
    }
    private static void FlushRename(string source, string destination)
    {
        FlushDirectory(Path.GetDirectoryName(destination)!);
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination)))
            FlushDirectory(Path.GetDirectoryName(source)!);
    }
    public static void FlushDirectory(string directory)
    {
        // Windows renames use MOVEFILE_WRITE_THROUGH. Unix additionally needs durable directory entries.
        if (OperatingSystem.IsWindows()) return;
        var descriptor = OpenDirectory(directory, 0);
        if (descriptor < 0) throw new IOException($"Невозможно открыть каталог для фиксации (код ОС {Marshal.GetLastPInvokeError()})");
        try { if (Sync(descriptor) != 0) throw new IOException($"Не удалось зафиксировать каталог (код ОС {Marshal.GetLastPInvokeError()})"); }
        finally { Close(descriptor); }
    }
    // MoveFileEx without COPY_ALLOWED cannot turn a rename into an unchecked cross-volume copy/delete.
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveWindows(string source, string destination, uint flags);
    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)] private static extern int MoveMac(string source, string destination, uint flags);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] private static extern int MoveLinux(int oldDirectory, string source, int newDirectory, string destination, uint flags);
    public static bool TryMove(string source, string destination)
    {
        int error;
        if (OperatingSystem.IsWindows())
        {
            if (MoveWindows(source, destination, 8)) return true;
            error = Marshal.GetLastPInvokeError();
            if (error == 17) return false; // ERROR_NOT_SAME_DEVICE
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (MoveMac(source, destination, 4) == 0) { FlushRename(source, destination); return true; } // RENAME_EXCL
            error = Marshal.GetLastPInvokeError();
            if (error == 18) return false; // EXDEV
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                if (MoveLinux(-100, source, -100, destination, 1) == 0) { FlushRename(source, destination); return true; } // RENAME_NOREPLACE
                error = Marshal.GetLastPInvokeError();
                if (error is 18 or 38 or 22) return false; // cross-volume or unavailable primitive => verified copy
            }
            catch (EntryPointNotFoundException) { return false; }
        }
        else return false;
        throw new IOException($"Безопасное переименование не выполнено (код ОС {error}); исходный файл сохранён");
    }
}


internal static class WindowsFileStreams
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr First(string path, int informationLevel, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Next(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
    public static void RequirePlainFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        if ((File.GetAttributes(path) & FileAttributes.Encrypted) != 0)
            throw new IOException("Перенос EFS-файла между томами пока не поддержан; зашифрованный оригинал сохранён");
        var handle = First(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is 38 or 87) return; // no streams / filesystem does not support streams, documented by Win32
            throw new IOException($"Не удалось проверить дополнительные потоки файла (код ОС {error}); оригинал сохранён");
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA")
                    throw new IOException("У файла есть дополнительные потоки NTFS. Пока поддержан только перенос внутри тома; оригинал сохранён");
            } while (Next(handle, out data));
            var error = Marshal.GetLastPInvokeError();
            if (error != 38) throw new IOException($"Не удалось завершить проверку потоков файла (код ОС {error}); оригинал сохранён");
        }
        finally { FindClose(handle); }
    }
}
