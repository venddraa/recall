using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32.SafeHandles;
using Recall.Core.Scanning;

namespace Recall.Core.Extraction;

internal static class ReadOnlyFileAccess
{
    internal static bool CanReuse(ScannedFile file, int maximumFileBytes)
    {
        try
        {
            if (file.Size > maximumFileBytes || HasSymbolicLink(file.FullPath))
                return false;
            using var stream = Open(file.FullPath);
            var info = new FileInfo(file.FullPath);
            return info.Exists && info.Length == file.Size && info.LastWriteTimeUtc == file.ModifiedAtUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    internal static bool HasSymbolicLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return true;
        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    internal static FileStream Open(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // A FIFO must never block indexing. O_NOFOLLOW also rejects a replaced file symlink.
        var descriptor = NativeMethods.Open(path, 0x80000 | 0x800 | 0x20000); // CLOEXEC | NONBLOCK | NOFOLLOW, RDONLY = 0
        if (descriptor < 0)
            throw new IOException("Could not open the file read-only.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (NativeMethods.Statx(descriptor, "", 0x1000, 1, out var stat) != 0 || (stat.Mode & 0xf000) != 0x8000)
                throw new IOException("Only regular files can be read for content indexing.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static (uint DeviceMajor, uint DeviceMinor, ulong Inode)? DirectoryIdentity(string path)
    {
        if (!OperatingSystem.IsLinux())
            return null;
        // AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_INO. Layout is defined in linux/stat.h.
        if (NativeMethods.Statx(-100, path, 0x100, 0x101, out var stat) != 0
            || (stat.Mode & 0xf000) != 0x4000 || (stat.Mask & 0x100) == 0)
            throw new IOException("Could not identify the monitored directory.");
        return (stat.DeviceMajor, stat.DeviceMinor, stat.Inode);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct FileStat
        {
            [FieldOffset(0)] internal uint Mask;
            [FieldOffset(28)] internal ushort Mode;
            [FieldOffset(32)] internal ulong Inode;
            [FieldOffset(136)] internal uint DeviceMajor;
            [FieldOffset(140)] internal uint DeviceMinor;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
        internal static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags, uint mask, out FileStat stat);
    }
}
