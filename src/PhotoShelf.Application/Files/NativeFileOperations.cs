using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoShelf.Application.Files;

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
