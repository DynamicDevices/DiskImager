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

            if(!File.Exists(fileName))
                throw new ArgumentException(fileName + " doesn't exist");

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
            // Lock logical drive
            //
            var success = _diskAccess.LockDrive(driveLetter);
            if (!success)
            {
                LogMsg(@"Failed to lock drive");
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

            var fileLength = new FileInfo(fileName).Length;

            var uncompressedlength = fileLength;

            var errored = true;

            using (var basefs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                Stream fs;

                switch (eCompType)
                {
                    case EnumCompressionType.Zip:
                        {
                            var zipFile = new ZipFile(basefs);
                
                            var ze = (from ZipEntry zipEntry in zipFile
                                          where zipEntry.IsFile
                                          select zipEntry).FirstOrDefault();

                            if(ze == null)
                            {
                                LogMsg(@"Error reading zip input stream");
                                goto readfail2;                                
                            }

                            var zis = zipFile.GetInputStream(ze);

                            uncompressedlength = ze.Size;

                            fs = zis;
                        }
                        break;

                    case EnumCompressionType.Gzip:
                        {
                            var gzis = new GZipInputStream(basefs) {IsStreamOwner = true};

                            uncompressedlength = gzis.Length;

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

                            uncompressedlength = tarEntry.Size;

                            fs = tis;
                        }
                        break;

                    default:

                        // No compression - direct to file stream
                        fs = basefs;

                        uncompressedlength = fs.Length;

                        break;
                }

                var bufferOffset = 0;

                using (var br = new BinaryReader(fs))
                {
                    while (offset < uncompressedlength && !IsCancelling)
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

                        var percentDone = (int)(100 * offset / uncompressedlength);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset / tsElapsed.TotalSeconds;

                        Progress(percentDone);
                        
                        LogMsg(@"Wrote " + percentDone + @"%, " + (offset / (1024 * 1024)) + @" MB / " +
                                                     (uncompressedlength / (1024 * 1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec / (1024 * 1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss")));
                    }
                }
            }
            errored = false;

        readfail1:
            _diskAccess.Close();
        readfail2:
            _diskAccess.UnlockDrive();

            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                LogMsg("Cancelled");
            else 
                LogMsg("All Done - Wrote " + offset + " bytes. Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));

            Progress(0);
            return !errored;
        }

        /// <summary>
        /// Read data direct from drive to file
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <param name="eCompType"></param>
        /// <returns></returns>
        public bool ReadDrive(string driveLetter, string fileName, EnumCompressionType eCompType, bool bUseMBR)
        {
            IsCancelling = false;

            var dtStart = DateTime.Now;

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
            // Lock logical drive
            //
            var success = _diskAccess.LockDrive(driveLetter);
            if (!success)
            {
                LogMsg(@"Failed to lock drive");
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

            var readSize = driveSize;

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

                            fs = tos;
                        }
                        break;

                    default:

                        // No compression - direct to file stream
                        fs = basefs;
                        break;
                }

                
                    while (offset < readSize && !IsCancelling)
                    {
                        // NOTE: If we provide a buffer that extends past the end of the physical device ReadFile() doesn't
                        //       seem to do a partial read. Deal with this by reading the remaining bytes at the end of the
                        //       drive if necessary

                        var readMaxLength =
                            (int)
                            ((((ulong) readSize - (ulong) offset) < (ulong) buffer.Length)
                                 ? ((ulong) readSize - (ulong) offset)
                                 : (ulong) buffer.Length);

                        int readBytes;
                        if (_diskAccess.Read(buffer, readMaxLength, out readBytes) < 0)
                        {
                            LogMsg(@"Error reading data from drive: " +
                                           Marshal.GetHRForLastWin32Error());
                            goto readfail1;
                        }

                        if (readBytes == 0)
                        {
                            LogMsg(@"Error reading data from drive - past EOF?");
                            goto readfail1;
                        }

                        // Check MBR
                        if (bUseMBR && offset == 0)
                        {
                            var truncatedSize = ParseMBRForSize(buffer);
                            
                            if(truncatedSize > driveSize)
                            {
                                LogMsg("Problem with filesystem. It reports it is larger than the disk!");
                                goto readfail1;
                            }

                            if(truncatedSize == 0)
                            {
                                LogMsg("No valid partitions on drive");
                                goto readfail1;
                            }

                            readSize = truncatedSize;
                        }

                        if(offset == 0)
                        {
                            switch (eCompType)
                            {
                                case EnumCompressionType.Targzip:
                                    var fi = new FileInfo(fileName);
                                    var entryName = fi.Name;
                                    entryName = entryName.ToLower().Replace(".tar.gz", "");
                                    entryName = entryName.ToLower().Replace(".tgz", "");

                                    var tarEntry = TarEntry.CreateTarEntry(entryName);
                                    tarEntry.Size = readSize;
                                    tarEntry.ModTime = DateTime.SpecifyKind(fi.LastWriteTime, DateTimeKind.Utc);

                                    ((TarOutputStream) fs).PutNextEntry(tarEntry);

                                    break;
                            }
                        }

                        fs.Write(buffer, 0, readBytes);

                        offset += (uint) readBytes;

                        var percentDone = (int) (100*offset/readSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset/tsElapsed.TotalSeconds;

                        Progress(percentDone);
                        LogMsg(@"Read " + percentDone + @"%, " + (offset/(1024*1024)) + @" MB / " +
                                       (readSize/(1024*1024) + " MB (Physical: " + (driveSize/(1024*1024)) + " MB), " +
                                        string.Format("{0:F}", (bytesPerSec/(1024*1024))) + @" MB/sec, Elapsed time: " +
                                        tsElapsed.ToString(@"dd\.hh\:mm\:ss")));

                    }
                
                    // Todo: Do we need this?
                    if (fs is ZipOutputStream)
                    {
                        ((ZipOutputStream)fs).CloseEntry();
                        ((ZipOutputStream)fs).Close();
                    }
                    if (fs is TarOutputStream)
                    {
                        ((TarOutputStream) fs).CloseEntry();
                        ((TarOutputStream) fs).Close();
                    }
                    if (fs is GZipOutputStream)
                    {
   //                    ((GZipOutputStream) fs).Finish();
                        ((GZipOutputStream) fs).Close();
                    }

            }

        readfail1:
            
            _diskAccess.Close();
            
            _diskAccess.UnlockDrive();
            
            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (IsCancelling)
                LogMsg("Cancelled");
            else
                LogMsg("All Done - Read " + offset + " bytes. Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss"));
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
        
        private uint ParseMBRForSize(byte[] buffer)
        {
            var pinnedInfos = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            var mbr = (MBR)Marshal.PtrToStructure(pinnedInfos.AddrOfPinnedObject(), typeof(MBR));
            pinnedInfos.Free();

            if (mbr.signature != 0xAA55)
            {
                LogMsg("Problem: MBR signature is not correct");
                return 0;
            }

            uint end = 0;

            if(mbr.partition1.type != EnumPartitionType.EMPTY)
            {
                end = (mbr.partition1.sectorsFromMBRToPartition + mbr.partition1.sectorsInPartition)*512;
            }
            if (mbr.partition2.type != EnumPartitionType.EMPTY)
            {
                end = (mbr.partition2.sectorsFromMBRToPartition + mbr.partition2.sectorsInPartition) * 512;
            }
            if (mbr.partition3.type != EnumPartitionType.EMPTY)
            {
                end = (mbr.partition3.sectorsFromMBRToPartition + mbr.partition3.sectorsInPartition) * 512;
            }
            if (mbr.partition4.type != EnumPartitionType.EMPTY)
            {
                end = (mbr.partition4.sectorsFromMBRToPartition + mbr.partition4.sectorsInPartition) * 512;
            }

            return end;
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack=1)]
    struct MBR
    {
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=446)]
        public byte[] bootCode;

        public PBR partition1;
        public PBR partition2;
        public PBR partition3;
        public PBR partition4;

        public ushort signature;
    }

    enum EnumPartitionState : byte
    {
      INACTIVE = 0,
      ACTIVE = 0x80,
    }

    /*
         0  Empty           24  NEC DOS         81  Minix / old Lin bf  Solaris
         1  FAT12           27  Hidden NTFS Win 82  Linux swap / So c1  DRDOS/sec (FAT-
         2  XENIX root      39  Plan 9          83  Linux           c4  DRDOS/sec (FAT-
         3  XENIX usr       3c  PartitionMagic  84  OS/2 hidden C:  c6  DRDOS/sec (FAT-
         4  FAT16 <32M      40  Venix 80286     85  Linux extended  c7  Syrinx
         5  Extended        41  PPC PReP Boot   86  NTFS volume set da  Non-FS data
         6  FAT16           42  SFS             87  NTFS volume set db  CP/M / CTOS / .
         7  HPFS/NTFS/exFAT 4d  QNX4.x          88  Linux plaintext de  Dell Utility
         8  AIX             4e  QNX4.x 2nd part 8e  Linux LVM       df  BootIt
         9  AIX bootable    4f  QNX4.x 3rd part 93  Amoeba          e1  DOS access
         a  OS/2 Boot Manag 50  OnTrack DM      94  Amoeba BBT      e3  DOS R/O
         b  W95 FAT32       51  OnTrack DM6 Aux 9f  BSD/OS          e4  SpeedStor
         c  W95 FAT32 (LBA) 52  CP/M            a0  IBM Thinkpad hi eb  BeOS fs
         e  W95 FAT16 (LBA) 53  OnTrack DM6 Aux a5  FreeBSD         ee  GPT
         f  W95 Ext'd (LBA) 54  OnTrackDM6      a6  OpenBSD         ef  EFI (FAT-12/16/
        10  OPUS            55  EZ-Drive        a7  NeXTSTEP        f0  Linux/PA-RISC b
        11  Hidden FAT12    56  Golden Bow      a8  Darwin UFS      f1  SpeedStor
        12  Compaq diagnost 5c  Priam Edisk     a9  NetBSD          f4  SpeedStor
        14  Hidden FAT16 <3 61  SpeedStor       ab  Darwin boot     f2  DOS secondary
        16  Hidden FAT16    63  GNU HURD or Sys af  HFS / HFS+      fb  VMware VMFS
        17  Hidden HPFS/NTF 64  Novell Netware  b7  BSDI fs         fc  VMware VMKCORE
        18  AST SmartSleep  65  Novell Netware  b8  BSDI swap       fd  Linux raid auto
        1b  Hidden W95 FAT3 70  DiskSecure Mult bb  Boot Wizard hid fe  LANstep
        1c  Hidden W95 FAT3 75  PC/IX           be  Solaris boot    ff  BBT
        1e  Hidden W95 FAT1 80  Old Minix
    */

    enum EnumPartitionType : byte
    {
       EMPTY = 0,
       FAT12 = 1,
       FAT16_LESS_THAN_32MB = 0x04, 
       EXT_MSDOS = 0x05,
       FAT16_GREATER_THAN_32MB = 0x06, 
       FAT32 = 0x0B,
       FAT32_LBA = 0x0C,
       FAT16_LBA = 0x0E,
       EXT_MSDOS_LBA = 0x0F,
       LINUX_EXT2 = 0x83,
       LINUX_SWAP = 0x82,
    }
       
    [StructLayout(LayoutKind.Sequential, Pack=1)]
    struct PBR
    {
        public EnumPartitionState state;

        public byte startHead;

        public byte startCylinderHighSector;

        public byte startCylinderLow;

        public EnumPartitionType type;

        public byte endHead;

        public byte endCylinderHighSector;

        public byte endCylinderLow;

        public uint sectorsFromMBRToPartition;

        public uint sectorsInPartition;

        public byte GetStartHead()
        {
            return startHead;
        }

        public ushort GetStartSector()
        {
            return (ushort)(startCylinderHighSector & 0x3F);
        }

        public ushort GetStartCylinder()
        {
            return (ushort)((((ushort)(startCylinderHighSector & 0xC0)) << 2) +startCylinderLow);
        }

        public byte GetEndHead()
        {
            return endHead;
        }

        public ushort GetEndSector()
        {
            return (ushort)(endCylinderHighSector & 0x3F);
        }

        public ushort GetEndCylinder()
        {
            return (ushort)((((ushort)(endCylinderHighSector & 0xC0)) << 2) + endCylinderLow);
        }

        public uint GetStartLBA()
        {
            return (uint) GetStartCylinder()*GetStartHead()*GetStartSector();
        }

        public uint GetEndLBA()
        {
            return (uint)GetEndCylinder() * GetEndHead() * GetEndSector();
        }

        public uint GetStartBytes()
        {
            return GetStartLBA()*512;
        }

        public uint GetEndBytes()
        {
            return GetStartLBA() * 512;            
        }
    }
}