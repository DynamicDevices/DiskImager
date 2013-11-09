using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;
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
        /// <param name="eCompType"></param>
        /// <returns></returns>
        public bool WriteDrive(string driveLetter, string fileName, EnumCompressionType eCompType)
        {
            var success = false;
            int intOut;

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
                if (
                    !(from ManagementObject disk in disks select (string) disk["deviceid"]).Any(
                        thisDisk => thisDisk == driveLetter)) continue;
                diskIndex = (int)(UInt32)current["diskindex"]; ;

                //
                // Unmount partition (Todo: Note that we currntly only handle unmounting of one partition, which is the usual case for SD Cards)
                //

                //
                // Open the volume
                ///
                partitionHandle = NativeMethods.CreateFile(@"\\.\" + driveLetter, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (partitionHandle.IsInvalid)
                {
                    OnLogMsg(this, @"Failed to open device");
                    partitionHandle.Dispose();
                    return false;
                }

                //
                // Lock it
                //
                success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                if (!success)
                {
                    OnLogMsg(this, @"Failed to lock device");
                    partitionHandle.Dispose();
                    return false;
                }

                //
                // Dismount it
                //
                success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                if (!success)
                {
                    OnLogMsg(this, @"Error dismounting volume: " + Marshal.GetHRForLastWin32Error());
                    NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                    partitionHandle.Dispose();
                    return false;
                }
            }

            if (diskIndex < 0)
            {
                OnLogMsg(this, @"Error: Couldn't map partition to physical drive");
                goto readfail3;
            }

            success = false;

            var physicalDrive = @"\\.\PhysicalDrive" + diskIndex;

            //
            // Now that we've dismounted the logical volume mounted on the removable drive we can open up the physical disk to write
            //
            var diskHandle = NativeMethods.CreateFile(physicalDrive, NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                OnLogMsg(this, @"Failed to open device: " + Marshal.GetHRForLastWin32Error());
                goto readfail3;
            }

            //
            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)
            //

            var driveSize = GetDiskSize(diskHandle);

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                OnLogMsg(this, @"Failed to lock device");
                diskHandle.Dispose();
                return false;
            }

            var buffer = new byte[Globals.MaxBufferSize];
            long offset = 0;

            using (var basefs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {

                Stream fs;

                switch (eCompType)
                {
                    case EnumCompressionType.Zip:
                        {
                            var zipFile = new ZipFile(basefs);
                
                            Stream zis = null;

                            foreach(ZipEntry zipEntry in zipFile)
                            {
                                if (!zipEntry.IsFile)
                                    continue;

                                zis = zipFile.GetInputStream(zipEntry);
                                break;
                            }

                            if(zis == null)
                            {
                                OnLogMsg(this, @"Error reading zip input stream");
                                goto readfail2;                                
                            }

                            fs = zis;
                        }
                        break;

                    case EnumCompressionType.Gzip:
                        {
                            var gzis = new GZipInputStream(basefs) {IsStreamOwner = true};

                            fs = gzis;
                        }
                        break;

                    case EnumCompressionType.Targzip:
                        {
                            var gzos = new GZipInputStream(basefs);
                            gzos.IsStreamOwner = true;

                            var tis = new TarInputStream(gzos);

                            TarEntry tarEntry;
                            do
                            {
                                tarEntry = tis.GetNextEntry();
                            } while (tarEntry.IsDirectory);

                            fs = tis;
                        }
                        break;

                    default:

                        // No compression - direct to file stream
                        fs = basefs;
                        break;
                }

                var bufferOffset = 0;

                using (var br = new BinaryReader(fs))
                {
                    while (offset < driveSize && !IsCancelling)
                    {
                        // Note: There's a problem writing certain lengths to the underlying physical drive.
                        //       This appears when we try to read from a compressed stream as it gives us
                        //       "strange" lengths which then fail to be written via Writefile() so try to build
                        //       up a decent block of bytes here...
                        int readBytes = 0;
                        do
                        {
                            readBytes = br.Read(buffer, bufferOffset, buffer.Length - bufferOffset);
                            bufferOffset += readBytes;
                        } while (bufferOffset < Globals.MaxBufferSize && readBytes != 0);
 
                        int wroteBytes;
                        var bytesToWrite = bufferOffset;
                        var trailingBytes = 0;

                        // Assume that the underlying physical drive will at least accept powers of two!
                        if(!IsPowerOfTwo((ulong)bufferOffset))
                        {
                            // Find highest bit (32-bit max)
                            var highBit = 31;
                            for (; ((bufferOffset & (1 << highBit)) == 0) && highBit >= 0; highBit--)
                                ;

                            // Work out trailing bytes after last power of two
                            var lastPowerOf2 = 1 << highBit;

                            bytesToWrite = lastPowerOf2;
                            trailingBytes = bufferOffset - lastPowerOf2;
                        }

                        if (NativeMethods.WriteFile(diskHandle, buffer, bytesToWrite, out wroteBytes, IntPtr.Zero) < 0)
                        {
                            OnLogMsg(this, @"Error writing data to drive: " + Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }

                        if (wroteBytes != bytesToWrite)
                        {
                            OnLogMsg(this, @"Error writing data to drive - past EOF?");
                            goto readfail1;
                        }

                        // Move trailing bytes up - Todo: Suboptimal
                        if (trailingBytes > 0)
                        {
                            Buffer.BlockCopy(buffer, bufferOffset - trailingBytes, buffer, 0, trailingBytes);
                            bufferOffset = trailingBytes;
                        }
                        else
                        {
                            bufferOffset = 0;
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

        readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
        readfail2:            
            diskHandle.Dispose();
        readfail3:

            if (partitionHandle != null)
                partitionHandle.Dispose();

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
        /// <param name="eCompType"></param>
        /// <returns></returns>
        public bool ReadDrive(string driveLetter, string fileName, EnumCompressionType eCompType)
        {
            var success = false;
            int intOut;

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
            var driveSize = GetDiskSize(diskHandle);
            if(driveSize <= 0)
            {
                OnLogMsg(this, @"Failed to get device size");
                diskHandle.Dispose();
                return false;                
            }

            //
            // Lock the drive
            // 
            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                OnLogMsg(this, @"Failed to lock device");
                diskHandle.Dispose();
                return false;
            }

            //
            // Start doing the read
            //

            var buffer = new byte[Globals.MaxBufferSize];
            var offset = 0L;


            using(var basefs = (Stream)new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                Stream fs;

                switch (eCompType)
                {
                    case EnumCompressionType.Zip:
                        {
                            var zfs = new ZipOutputStream(basefs);

                            // Default to middle of the range compression
                            zfs.SetLevel(Globals.CompressionLevel);

                            var fi = new FileInfo(fileName);
                            var entryName = fi.Name;
                            entryName = entryName.ToLower().Replace(".zip", "");
                            entryName = ZipEntry.CleanName(entryName);
                            var zipEntry = new ZipEntry(entryName) {DateTime = fi.LastWriteTime};
                            zfs.IsStreamOwner = true;

                            // Todo: Consider whether size needs setting for older utils ?

                            zfs.PutNextEntry(zipEntry);

                            fs = zfs;
                        }
                        break;

                    case EnumCompressionType.Gzip:
                        {
                            var gzos = new GZipOutputStream(basefs);
                            gzos.SetLevel(Globals.CompressionLevel);
                            gzos.IsStreamOwner = true;

                            fs = gzos;
                        }
                        break;

                    case EnumCompressionType.Targzip:
                        {
                            var gzos = new GZipOutputStream(basefs);
                            gzos.SetLevel(Globals.CompressionLevel);
                            gzos.IsStreamOwner = true;

                            var tos = new TarOutputStream(gzos);

                            var fi = new FileInfo(fileName);
                            var entryName = fi.Name;
                            entryName = entryName.ToLower().Replace(".tar.gz", "");
                            entryName = entryName.ToLower().Replace(".tgz", "");

                            var tarEntry = TarEntry.CreateTarEntry(entryName);
                            tarEntry.Size = driveSize;
                            tarEntry.ModTime = DateTime.SpecifyKind(fi.LastWriteTime, DateTimeKind.Utc);

                            tos.PutNextEntry(tarEntry);

                            fs = tos;
                        }
                        break;

                    default:

                        // No compression - direct to file stream
                        fs = basefs;
                        break;
                }

                using (var bw = new BinaryWriter(fs))
                {
                    while (offset < driveSize && !IsCancelling)
                    {
                        // NOTE: If we provide a buffer that extends past the end of the physical device ReadFile() doesn't
                        //       seem to do a partial read. Deal with this by reading the remaining bytes at the end of the
                        //       drive if necessary

                        var readMaxLength =
                            (int)
                            ((((ulong) driveSize - (ulong) offset) < (ulong) buffer.Length)
                                 ? ((ulong) driveSize - (ulong) offset)
                                 : (ulong) buffer.Length);

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

                        offset += (uint) readBytes;

                        var percentDone = (int) (100*offset/driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset/tsElapsed.TotalSeconds;

                        OnProgress(this, percentDone);
                        OnLogMsg(this, @"Read " + percentDone + @"%, " + (offset/(1024*1024)) + @" MB / " +
                                       (driveSize/(1024*1024) + " MB, " +
                                        string.Format("{0:F}", (bytesPerSec/(1024*1024))) + @" MB/sec, Elapsed time: " +
                                        tsElapsed.ToString(@"dd\.hh\:mm\:ss")));

                    }
                
                    // Todo: Do we need this?
                    if(fs is ZipOutputStream)
                        ((ZipOutputStream)fs).CloseEntry();
                    if(fs is TarOutputStream)
                        ((TarOutputStream)fs).CloseEntry();
                }

            }

        readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
        readfail2:
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
        private static int GetDiskIndex(string driveLetter)
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
                if (
                    !(from ManagementObject disk in disks select (string) disk["deviceid"]).Any(
                        thisDisk => thisDisk == driveLetter)) continue;
                diskIndex = (int)(UInt32)current["diskindex"];
            }

            return diskIndex;
        }

        /// <summary>
        /// Returns size of physical disk
        /// </summary>
        /// <param name="diskHandle"></param>
        /// <returns></returns>
        private static long GetDiskSize(SafeFileHandle diskHandle)
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

        bool IsPowerOfTwo(ulong x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }

        #endregion
    }
}