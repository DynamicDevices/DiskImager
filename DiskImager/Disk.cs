using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicDevices.DiskWriter
{
    public delegate void TextHandler( object sender, string message);

    public delegate void ProgressHandler(object sender, int progressPercentage);

    internal class Disk
    {
        public bool IsCancelling { get; set;}

        public event TextHandler OnLogMsg;

        public event ProgressHandler OnProgress;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public bool WriteDrive(string driveLetter, string fileName)
        {
            var success = false;
            int intOut;
            long driveSize = 0;

            IsCancelling = false;

            var dtStart = DateTime.Now;

            var diskIndex = -1;

            SafeFileHandle partitionHandle = null;

            //
            // Get physical drive partition for logical partition
            //
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
                foreach (ManagementObject disk in disks)
                {
                    var thisDisk = (string)disk["deviceid"];
                    if (thisDisk == driveLetter)
                    {
                        // Grab physical drive and size
                        diskIndex = (int)(UInt32)current["diskindex"]; ;

                        // Unmount partition (todo: Note that we currntly only handle unmounting of one partition, which is the usual case for SD Cards)
                        partitionHandle = NativeMethods.CreateFile(@"\\.\" + driveLetter, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (partitionHandle.IsInvalid)
                        {
                            OnLogMsg(this, @"Failed to open device");
//                            NativeMethods.CloseHandle(partitionHandle);
                            partitionHandle.Dispose();
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            OnLogMsg(this, @"Failed to lock device");
//                            NativeMethods.CloseHandle(partitionHandle);
                            partitionHandle.Dispose();
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            OnLogMsg(this, @"Error dismounting volume: " + Marshal.GetHRForLastWin32Error());
                            NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
//                            NativeMethods.CloseHandle(partitionHandle);
                            partitionHandle.Dispose();
                            return false;
                        }

                        break;
                    }
                }
            }

            if (diskIndex < 0)
            {
                OnLogMsg(this, @"Error: Couldn't map partition to physical drive");
                goto readfail3;
            }

            success = false;

            var physicalDrive = @"\\.\PhysicalDrive" + diskIndex;

            var diskHandle = NativeMethods.CreateFile(physicalDrive, NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                OnLogMsg(this, @"Failed to open device: " + Marshal.GetHRForLastWin32Error());
                goto readfail3;
            }

            //
            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)
            //

            driveSize = GetDiskSize(diskHandle);

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                OnLogMsg(this, @"Failed to lock device");
//                NativeMethods.CloseHandle(diskHandle);
                diskHandle.Dispose();
                return false;
            }

            var buffer = new byte[Globals.MaxBufferSize];
            long offset = 0;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                using (var bw = new BinaryReader(fs))
                {
                    while (offset < driveSize && !IsCancelling)
                    {
                        var readBytes = bw.Read(buffer, 0, buffer.Length);

                        int wroteBytes;

                        if (NativeMethods.WriteFile(diskHandle, buffer, readBytes, out wroteBytes, IntPtr.Zero) < 0)
                        {
                            OnLogMsg(this, @"Error writing data to drive: " + Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }

                        if (wroteBytes != readBytes)
                        {
                            OnLogMsg(this, @"Error writing data to drive - past EOF?");
                            goto readfail1;
                        }

                        offset += (uint)wroteBytes;

                        var percentDone = (int)(100 * offset / driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset / tsElapsed.TotalSeconds;

                        OnProgress(this, percentDone);
                        
                        OnLogMsg(this, @"Wrote " + percentDone + @"%, " + (offset / (1024 * 1024)) + @" MB / " +
                                                     (driveSize / (1024 * 1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec / (1024 * 1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss")));
                    }
                }
            }

            success = true;

        readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
        readfail2:
            if (diskHandle != null)
            {
//                NativeMethods.CloseHandle(diskHandle);
                diskHandle.Dispose();
            }
        readfail3:

            if (partitionHandle != null)
            {
//                NativeMethods.CloseHandle(partitionHandle);
                partitionHandle.Dispose();
            }

            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                OnLogMsg(this, "Cancelled");
            else if (success)
                OnLogMsg(this, "All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));
            OnProgress(this, 0);
            return success;
        }

        /// <summary>
        /// Read data direct from drive to file
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public bool ReadDrive(string driveLetter, string fileName)
        {
            var success = false;
            int intOut;
            long driveSize = 0;

            IsCancelling = false;

            var dtStart = DateTime.Now;

            //
            // Get physical disk index for logical partition
            //
            var diskIndex = GetDiskIndex(driveLetter);
            if (diskIndex < 0)
            {
                OnLogMsg(this, @"Error: Couldn't map partition to physical drive");
                goto readfail3;
            }

            //
            // Open up physical drive
            // 
            var physicalDrive = @"\\.\PhysicalDrive" + diskIndex;

            var diskHandle = NativeMethods.CreateFile(physicalDrive, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                OnLogMsg(this, @"Failed to open device: " + Marshal.GetHRForLastWin32Error());
                goto readfail3;
            }

            //
            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)
            //
            driveSize = GetDiskSize(diskHandle);
            if(driveSize <= 0)
            {
                OnLogMsg(this, @"Failed to get device size");
                NativeMethods.CloseHandle(diskHandle);
                return false;                
            }

            //
            // Lock the drive
            // 
            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                OnLogMsg(this, @"Failed to lock device");
                NativeMethods.CloseHandle(diskHandle);
                return false;
            }

            //
            // Start doing the read
            //

            var buffer = new byte[Globals.MaxBufferSize];
            var offset = 0L;

            using (var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                using (var bw = new BinaryWriter(fs))
                {
                    while (offset < driveSize && !IsCancelling)
                    {
                        // NOTE: If we provide a buffer that extends past the end of the physical device ReadFile() doesn't
                        //       seem to do a partial read. Deal with this by reading the remaining bytes at the end of the
                        //       drive if necessary

                        var readMaxLength = (int)((((ulong)driveSize - (ulong)offset) < (ulong)buffer.Length) ? ((ulong)driveSize - (ulong)offset) : (ulong)buffer.Length);

                        int readBytes;
                        if (NativeMethods.ReadFile(diskHandle, buffer, readMaxLength, out readBytes, IntPtr.Zero) < 0)
                        {
                            OnLogMsg(this, @"Error reading data from drive: " +
                                                         Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }


                        bw.Write(buffer, 0, readBytes);

                        if (readBytes == 0)
                        {
                            OnLogMsg(this, @"Error reading data from drive - past EOF?");
                            goto readfail1;
                        }

                        offset += (uint)readBytes;

                        var percentDone = (int)(100 * offset / driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset / tsElapsed.TotalSeconds;

                        OnProgress(this, percentDone);
                        OnLogMsg(this, @"Read " + percentDone + @"%, " + (offset / (1024 * 1024)) + @" MB / " +
                                                     (driveSize / (1024 * 1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec / (1024 * 1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss")));

                    }
                }
            }

            success = true;

        readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
        readfail2:
//            NativeMethods.CloseHandle(diskHandle);
            diskHandle.Dispose();
        readfail3:
            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                OnLogMsg(this, "Cancelled");
            else if (success)
                OnLogMsg(this, "All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));
            OnProgress(this, 0);
            return success;
        }

        #region Support

        /// <summary>
        /// Returns physical drive index upon which the logical partition resides
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <returns></returns>
        private int GetDiskIndex(string driveLetter)
        {
            int diskIndex = -1;

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
                foreach (ManagementObject disk in disks)
                {
                    var thisDisk = (string)disk["deviceid"];
                    if (thisDisk == driveLetter)
                    {
                        // Grab physical drive and size
                        diskIndex = (int)(UInt32)current["diskindex"]; ;
                        break;
                    }
                }
            }

            return diskIndex;
        }

        /// <summary>
        /// Returns size of physical disk
        /// </summary>
        /// <param name="diskHandle"></param>
        /// <returns></returns>
        private long GetDiskSize(SafeFileHandle diskHandle)
        {
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

            return size;
        }

        #endregion
    }
}