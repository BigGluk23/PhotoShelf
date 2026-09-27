using System.Runtime.InteropServices;

namespace PhotoShelf.Infrastructure.Sqlite;

/// <summary>Publish only the verified backup; never silently overwrite an older snapshot.</summary>
internal static class CatalogBackupPublication
{
    [DllImport("kernel32.dll", EntryPoint="MoveFileExW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindows(string source,string destination,uint flags);
    [DllImport("libc",EntryPoint="open",SetLastError=true)]private static extern int OpenDirectory(string path,int flags);
    [DllImport("libc",EntryPoint="fsync",SetLastError=true)]private static extern int Sync(int descriptor);
    [DllImport("libc",EntryPoint="close",SetLastError=true)]private static extern int Close(int descriptor);

    public static void Complete(string pending,string destination)
    {
        if(OperatingSystem.IsWindows())
        {
            // WRITE_THROUGH, deliberately without REPLACE_EXISTING or COPY_ALLOWED.
            if(!MoveWindows(pending,destination,0x8))
                throw new IOException($"Verified catalog backup could not be published durably (OS error {Marshal.GetLastPInvokeError()}); catalog migration was not started.");
            return;
        }
        File.Move(pending,destination);
        var directory=Path.GetDirectoryName(Path.GetFullPath(destination))!;
        FlushDirectory(directory);
        // The backups directory itself may have been created by this operation.
        if(Path.GetDirectoryName(directory) is { } parent)FlushDirectory(parent);
    }
    private static void FlushDirectory(string path)
    {
        var descriptor=OpenDirectory(path,0);
        if(descriptor<0)throw new IOException($"Cannot open backup directory for durable publication (OS error {Marshal.GetLastPInvokeError()}).");
        try
        {
            if(Sync(descriptor)!=0)throw new IOException($"Cannot flush backup directory (OS error {Marshal.GetLastPInvokeError()}).");
        }
        finally{Close(descriptor);}
    }
}
