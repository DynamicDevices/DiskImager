using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DynamicDevices.DiskWriter.Win32
{
    internal class LinuxDiskAccess : IDiskAccess
    {
        #region IDiskAccess Members

        public event TextHandler OnLogMsg;

        public event ProgressHandler OnProgress;

        public event EventHandler OnDiskChanged;

        public bool StartListenForChanges()
        {
            throw new NotImplementedException();            
        }

        public void StopListenForChanges()
        {
            throw new NotImplementedException();            
        }

        public Handle Open(string drivePath)
        {
            throw new NotImplementedException();
        }

        public bool LockDrive(string drivePath)
        {
            throw new NotImplementedException();
        }


        public void UnlockDrive()
        {
            throw new NotImplementedException();
        }

        public int Read(byte[] buffer, int readMaxLength, out int readBytes)
        {
            throw new NotImplementedException();
        }

        public int Write(byte[] buffer, int bytesToWrite, out int wroteBytes)
        {
            throw new NotImplementedException();
        }

        public void Close()
        {
            throw new NotImplementedException();
        }

        public string GetPhysicalPathForLogicalPath(string logicalPath)
        {
            throw new NotImplementedException();
        }

        public long GetDriveSize(string drivePath)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
