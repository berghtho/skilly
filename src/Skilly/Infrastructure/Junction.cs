using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Skilly.Infrastructure;

public static class Junction
{
    private const int GenericWriteAccess = 0x40000000;
    private const int FileOpenExisting = 3;
    private const int FileFlagBackupSemantics = 0x02000000;
    private const int FileShareReadWriteDelete = 0x7;
    private const int FsctlSetReparsePoint = 0x000900A4;
    private const int ReparseTagMountPoint = unchecked((int)0xA0000003);

    public static void Create(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);

        var fullTarget = Path.GetFullPath(targetPath);
        var substituteName = @"\??\" + fullTarget;
        var substituteBytes = System.Text.Encoding.Unicode.GetBytes(substituteName);
        var printNameBytes = System.Text.Encoding.Unicode.GetBytes(fullTarget);

        const int headerSize = 8;
        const int mountPointFixedSize = 8;
        var pathBufferLength = substituteBytes.Length + 2 + printNameBytes.Length + 2;
        var bufferLength = headerSize + mountPointFixedSize + pathBufferLength;
        var buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            unsafe
            {
                new Span<byte>((void*)buffer, bufferLength).Clear();
                var p = (byte*)buffer;
                *(int*)p = ReparseTagMountPoint;
                *(short*)(p + 4) = (short)(mountPointFixedSize + pathBufferLength);
                var pathBuffer = p + headerSize;
                *(ushort*)pathBuffer = 0;
                *(ushort*)(pathBuffer + 2) = (ushort)substituteBytes.Length;
                *(ushort*)(pathBuffer + 4) = (ushort)(substituteBytes.Length + 2);
                *(ushort*)(pathBuffer + 6) = (ushort)printNameBytes.Length;
            }

            Marshal.Copy(substituteBytes, 0, buffer + headerSize + 8, substituteBytes.Length);
            Marshal.Copy(printNameBytes, 0, buffer + headerSize + 8 + substituteBytes.Length + 2, printNameBytes.Length);

            using var handle = CreateFile(
                junctionPath,
                GenericWriteAccess,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                FileOpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open '{junctionPath}' for reparse-point write.");
            }

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, bufferLength, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Setting the reparse point on '{junctionPath}' failed.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsJunctionTo(string candidatePath, string targetPath)
    {
        try
        {
            var info = new DirectoryInfo(candidatePath);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null || !resolved.Exists)
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(resolved.FullName).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName,
        int dwDesiredAccess,
        int dwShareMode,
        IntPtr lpSecurityAttributes,
        int dwCreationDisposition,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
