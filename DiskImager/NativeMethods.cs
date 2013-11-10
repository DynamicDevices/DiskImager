using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicDevices.DiskWriter
{

    public enum EMoveMethod : int
    {
        Begin = 0,
        Current = 1,
        End = 2
    }

    public static class NativeMethods 
    {
        internal const uint OPEN_EXISTING = 3;
        internal const uint GENERIC_WRITE = (0x40000000);
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint FSCTL_LOCK_VOLUME = 0x00090018;
        internal const uint FSCTL_UNLOCK_VOLUME = 0x0009001c;
        internal const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        internal const uint FILE_SHARE_READ = 0x1;
        internal const uint FILE_SHARE_WRITE = 0x2;
        internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x70000;
        internal const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x700a0;
        internal const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
        internal const uint BCM_SETSHIELD = 0x160C;
        internal const int INVALID_SET_FILE_POINTER = -1;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern internal IntPtr LoadIcon(IntPtr hInstance, string lpIconName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern internal IntPtr LoadLibrary(string lpFileName);

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern internal int SetFilePointer([In] SafeFileHandle hFile, [In] int lDistanceToMove,  ref int lpDistanceToMoveHigh, [In] EMoveMethod dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern internal  SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32", SetLastError = true)]
        static extern internal int ReadFile(SafeFileHandle handle, byte[] bytes, int numBytesToRead, out int numBytesRead, IntPtr overlapped_MustBeZero);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern internal int WriteFile(SafeFileHandle handle, byte[] bytes, int numBytesToWrite, out int numBytesWritten, IntPtr overlapped_MustBeZero);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern internal bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("Kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint dwIoControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, ref uint bytesReturned, IntPtr overlapped);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        static extern internal bool CloseHandle(SafeFileHandle handle);

        [DllImport("user32", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int SendMessage(IntPtr hWnd, UInt32 Msg, int wParam, IntPtr lParam);

    }
}