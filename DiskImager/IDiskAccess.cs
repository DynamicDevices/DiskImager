using System;

namespace DynamicDevices.DiskWriter
{
    internal interface IDiskAccess
    {
        event TextHandler OnLogMsg;

        event ProgressHandler OnProgress;

        event EventHandler OnDiskChanged;

        bool StartListenForChanges();

        void StopListenForChanges();

        Handle Open(string drivePath);

        bool LockDrive(string drivePath);

        void UnlockDrive();

        int Read(byte[] buffer, int readMaxLength, out int readBytes);

        int Write(byte[] buffer, int bytesToWrite, out int wroteBytes);

        void Close();

        string GetPhysicalPathForLogicalPath(string logicalPath);

        long GetDriveSize(string drivePath);
    }
}
