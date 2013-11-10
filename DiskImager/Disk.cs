using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.Zip;

namespace DynamicDevices.DiskWriter
{
    public delegate void TextHandler( object sender, string message);

    public delegate void ProgressHandler(object sender, int progressPercentage);

    internal class Disk
    {
        public bool IsCancelling { get; set;}

        private event TextHandler _onLogMsg;

        public event TextHandler OnLogMsg
        {
            add
            {
                _onLogMsg -= value;
                _onLogMsg += value;
                _diskAccess.OnLogMsg -= value;
                _diskAccess.OnLogMsg += value;
            }
            remove
            {
                _onLogMsg -= value;
                _diskAccess.OnLogMsg -= value;
            }
        }

        private event ProgressHandler _onProgress;

        public event ProgressHandler OnProgress
        {
            add
            {
                _onProgress -= value;
                _onProgress += value;
                _diskAccess.OnProgress += value;
            }
            remove
            {
                _onProgress -= value;
                _diskAccess.OnProgress -= value; 
            }
        }

        private readonly IDiskAccess _diskAccess;

        /// <summary>
        /// Construct Disk object with underlying platform specific disk access implementation
        /// </summary>
        /// <param name="diskAccess"></param>
        public Disk(IDiskAccess diskAccess)
        {
            _diskAccess = diskAccess;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <param name="eCompType"></param>
        /// <returns></returns>
        public bool WriteDrive(string driveLetter, string fileName, EnumCompressionType eCompType)
        {
            IsCancelling = false;

            var dtStart = DateTime.Now;

            //
            // Lock logical drive
            //
            var success = _diskAccess.LockDrive(driveLetter);
            if (!success)
            {
                LogMsg(@"Failed to lock drive");
                return false;
            }            
            
            //
            // Get physical drive partition for logical partition
            // 
            var physicalDrive = _diskAccess.GetPhysicalPathForLogicalPath(driveLetter);
            if (string.IsNullOrEmpty(physicalDrive))
            {
                LogMsg(@"Error: Couldn't map partition to physical drive");
                _diskAccess.UnlockDrive();
                return false;
            }

            //
            // Get drive size 
            //
            var driveSize = _diskAccess.GetDriveSize(physicalDrive);
            if (driveSize <= 0)
            {
                LogMsg(@"Failed to get device size");
                _diskAccess.UnlockDrive();
                return false;
            }

            //
            // Open the physical drive
            // 
            var physicalHandle = _diskAccess.Open(physicalDrive);
            if (physicalHandle == null)
            {
                LogMsg(@"Failed to open physical drive");
                _diskAccess.UnlockDrive();
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
                
                            Stream zis = (from ZipEntry zipEntry in zipFile
                                          where zipEntry.IsFile
                                          select zipFile.GetInputStream(zipEntry)).FirstOrDefault();

                            if(zis == null)
                            {
                                LogMsg(@"Error reading zip input stream");
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
                            var gzos = new GZipInputStream(basefs) {IsStreamOwner = true};

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
                        int readBytes;
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

                        if (_diskAccess.Write(buffer, bytesToWrite, out wroteBytes) < 0)
                        {
                            LogMsg(@"Error writing data to drive: " + Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }

                        if (wroteBytes != bytesToWrite)
                        {
                            LogMsg(@"Error writing data to drive - past EOF?");
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

                        Progress(percentDone);
                        
                        LogMsg(@"Wrote " + percentDone + @"%, " + (offset / (1024 * 1024)) + @" MB / " +
                                                     (driveSize / (1024 * 1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec / (1024 * 1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss")));
                    }
                }
            }

        readfail1:
            _diskAccess.Close();
        readfail2:            

            _diskAccess.UnlockDrive();

            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                LogMsg("Cancelled");
            else 
                LogMsg("All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));
            Progress(0);
            return true;
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
            IsCancelling = false;

            var dtStart = DateTime.Now;

            //
            // Lock logical drive
            //
            var success = _diskAccess.LockDrive(driveLetter);
            if(!success)
            {
                LogMsg(@"Failed to lock drive");
                return false;                                
            }

            //
            // Map to physical drive
            // 
            var physicalDrive = _diskAccess.GetPhysicalPathForLogicalPath(driveLetter);
            if(string.IsNullOrEmpty(physicalDrive))
            {
                LogMsg(@"Error: Couldn't map partition to physical drive");
                _diskAccess.UnlockDrive();
                return false;
            }

            //
            // Get drive size 
            //
            var driveSize = _diskAccess.GetDriveSize(physicalDrive);
            if(driveSize <= 0)
            {
                LogMsg(@"Failed to get device size");
                _diskAccess.UnlockDrive();
                return false;                
            }

            //
            // Open the physical drive
            // 
            var physicalHandle = _diskAccess.Open(physicalDrive);
            if (physicalHandle == null)
            {
                LogMsg(@"Failed to open physical drive");
                _diskAccess.UnlockDrive();
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
                        if (_diskAccess.Read(buffer, readMaxLength, out readBytes) < 0)
                        {
                            LogMsg(@"Error reading data from drive: " +
                                           Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }

                        bw.Write(buffer, 0, readBytes);

                        if (readBytes == 0)
                        {
                            LogMsg(@"Error reading data from drive - past EOF?");
                            goto readfail1;
                        }

                        offset += (uint) readBytes;

                        var percentDone = (int) (100*offset/driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset/tsElapsed.TotalSeconds;

                        Progress(percentDone);
                        LogMsg(@"Read " + percentDone + @"%, " + (offset/(1024*1024)) + @" MB / " +
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
            
            _diskAccess.Close();
            
            _diskAccess.UnlockDrive();
            
            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                LogMsg("Cancelled");
            else 
                LogMsg("All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));
            Progress(0);
            return true;
        }

        #region Support

        private static bool IsPowerOfTwo(ulong x)
        {
            return (x != 0) && ((x & (x - 1)) == 0);
        }

        private void Progress(int progressValue)
        {
            if (_onProgress != null)
                _onProgress(this, progressValue);
        }

        private void LogMsg(string msg)
        {
            if (_onLogMsg != null)
                _onLogMsg(this, msg);
        }
        
        #endregion
    }
}