using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicDevices.DiskWriter.Win32
{
    internal class Win32DiskAccess : IDiskAccess
    {
        #region Fields

        SafeFileHandle _partitionHandle = null;
        SafeFileHandle _diskHandle = null;
        ManagementEventWatcher _watcher = new ManagementEventWatcher();

        #endregion

        #region IDiskAccess Members

        public event TextHandler OnLogMsg;

        public event ProgressHandler OnProgress;

        public event EventHandler OnDiskChanged;

        public bool StartListenForChanges()
        {
            var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
            _watcher.EventArrived += WatcherEventArrived;
            _watcher.Query = query;
            _watcher.Start();
            return true;
        }

        public void StopListenForChanges()
        {
            if(_watcher != null)
            {
                _watcher.Stop();
                _watcher = null;
            }
        }

        void  WatcherEventArrived(object sender, EventArrivedEventArgs e)
        {
            if(OnDiskChanged != null)
                OnDiskChanged(sender, e);
        }

        public Handle Open(string drivePath)
        {
            int intOut;

            //
            // Now that we've dismounted the logical volume mounted on the removable drive we can open up the physical disk to write
            //
            var diskHandle = NativeMethods.CreateFile(drivePath, NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                LogMsg(@"Failed to open device: " + Marshal.GetHRForLastWin32Error());
                return null;
            }

            var success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                LogMsg(@"Failed to lock device");
                diskHandle.Dispose();
                return null;
            }

            _diskHandle = diskHandle;

            return new Handle();
        }

        public bool LockDrive(string drivePath)
        {
            bool success;
            int intOut;
            SafeFileHandle partitionHandle;

            //
            // Unmount partition (Todo: Note that we currently only handle unmounting of one partition, which is the usual case for SD Cards)
            //

            //
            // Open the volume
            ///
            partitionHandle = NativeMethods.CreateFile(@"\\.\" + drivePath, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (partitionHandle.IsInvalid)
            {
                LogMsg(@"Failed to open device");
                partitionHandle.Dispose();
                return false;
            }

            //
            // Lock it
            //
            success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                LogMsg(@"Failed to lock device");
                partitionHandle.Dispose();
                return false;
            }

            //
            // Dismount it
            //
            success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                LogMsg(@"Error dismounting volume: " + Marshal.GetHRForLastWin32Error());
                NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                partitionHandle.Dispose();
                return false;
            }

            _partitionHandle = partitionHandle;

            return true;
        }


        public void UnlockDrive()
        {
            if(_partitionHandle != null)
            {
                _partitionHandle.Dispose();
                _partitionHandle = null;
            }
        }

        public int Read(byte[] buffer, int readMaxLength, out int readBytes)
        {
            readBytes = 0;

            if(_diskHandle == null)
                return -1;

            return NativeMethods.ReadFile(_diskHandle, buffer, readMaxLength, out readBytes, IntPtr.Zero);
        }

        public int Write(byte[] buffer, int bytesToWrite, out int wroteBytes)
        {
            wroteBytes = 0;
            if(_diskHandle == null)
                return -1;

            return NativeMethods.WriteFile(_diskHandle, buffer, bytesToWrite, out wroteBytes, IntPtr.Zero);
        }

        public void Close()
        {
            if (_diskHandle != null)
            {
                _diskHandle.Dispose();
                _diskHandle = null;
            }
        }

        public string GetPhysicalPathForLogicalPath(string logicalPath)
        {
            int diskIndex = -1;

            logicalPath = logicalPath.Trim(new[] {'\\'});
            
            var scope = new ManagementScope(@"\root\cimv2");
            var query = new ObjectQuery("select * from Win32_DiskPartition");
            var searcher = new ManagementObjectSearcher(scope, query);
            var drives = searcher.Get();

            foreach (var current in drives)
            {
                var associators =
                    new ObjectQuery("ASSOCIATORS OF {Win32_DiskPartition.DeviceID=\"" + current["deviceid"] +
                                    "\"} where assocclass=Win32_LogicalDiskToPartition");
                searcher = new ManagementObjectSearcher(scope, associators);
                var disks = searcher.Get();
                if (
                    !(from ManagementObject disk in disks select (string)disk["deviceid"]).Any(
                        thisDisk => thisDisk == logicalPath)) continue;
                diskIndex = (int)(UInt32)current["diskindex"];
            }

            var path = "";
            if(diskIndex >= 0)
                path = @"\\.\PhysicalDrive" + diskIndex;

            return path;
        }

        public long GetDriveSize(string drivePath)
        {
            //
            // Now that we've dismounted the logical volume mounted on the removable drive we can open up the physical disk to write
            //
            var diskHandle = NativeMethods.CreateFile(drivePath, NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                LogMsg( @"Failed to open device: " + Marshal.GetHRForLastWin32Error());
                return -1;
            }

            //
            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)
            //
            long size = -1;

            var geometrySize = Marshal.SizeOf(typeof(DiskGeometryEx));
            var geometryBlob = Marshal.AllocHGlobal(geometrySize);
            uint numBytesRead = 0;

            var success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero,
                                                    0, geometryBlob, (uint)geometrySize, ref numBytesRead, IntPtr.Zero);

            var geometry = (DiskGeometryEx)Marshal.PtrToStructure(geometryBlob, typeof(DiskGeometryEx));
            if (success)
                size = geometry.DiskSize;

            Marshal.FreeHGlobal(geometryBlob);

            diskHandle.Dispose();

            return size;
        }

        #endregion

        private void Progress(int progressValue)
        {
            if (OnProgress != null)
                OnProgress(this, progressValue);
        }

        private void LogMsg(string msg)
        {
            if (OnLogMsg != null)
                OnLogMsg(this, msg);
        }

    }
}
