using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Management;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DynamicDevices.DiskWriter
{
    public partial class MainForm : Form
    {
        private readonly ManagementEventWatcher _watcher = new ManagementEventWatcher();

        private const int MAX_BUFFER_SIZE = 1*1024*1024;

        private bool _isCancelling;

        public MainForm()
        {
            InitializeComponent();

            toolStripStatusLabel1.Text = @"OK";

            // Set version into title
            var version = Assembly.GetEntryAssembly().GetName().Version;
            Text += @" v" + version;

            // Set app icon
            Icon = Utility.GetAppIcon();

            PopulateDrives();

            // Read registry values
            var key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Dynamic Devices Ltd\\DiskImager");
            if (key != null)
            {
                textBoxFileName.Text = (string)key.GetValue("FileName", "");

                var drive = (string)key.GetValue("Drive", "");
                if (string.IsNullOrEmpty(drive))
                {
                    foreach(var cbDrive in comboBoxDrives.Items)
                    {
                        if(cbDrive == drive)
                        {
                            comboBoxDrives.SelectedItem = cbDrive;
                        }
                    }
                }
                key.Close();
            }

            // Detect insertions
            var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
            _watcher.EventArrived += WatcherEventArrived;
            _watcher.Query = query;
            _watcher.Start();
        }

        public override sealed string Text
        {
            get { return base.Text; }
            set { base.Text = value; }
        }

        private void PopulateDrives()
        {
            if (InvokeRequired)
            {
                Invoke(new MethodInvoker(PopulateDrives));
                return;
            }

            comboBoxDrives.SelectedIndex = -1;
            comboBoxDrives.Items.Clear();

            foreach (var drive in DriveInfo.GetDrives())
            {
                // Only display removable drives
                if (drive.DriveType == DriveType.Removable)
                {
                    comboBoxDrives.Items.Add(drive.Name.TrimEnd(new [] {'\\'}));
                }
            }

            if (comboBoxDrives.Items.Count > 0)
                comboBoxDrives.SelectedIndex = 0;
        }

        void WatcherEventArrived(object sender, EventArrivedEventArgs e)
        {
            PopulateDrives();
        }

        private void ButtonExitClick(object sender, EventArgs e)
        {
            Close();
        }

        private void ButtonChooseFileClick(object sender, EventArgs e)
        {
            saveFileDialog1.OverwritePrompt = false;

            var dr = saveFileDialog1.ShowDialog();
            if (dr != DialogResult.OK)
                return;

            textBoxFileName.Text = saveFileDialog1.FileName;
        }

        private void ButtonReadClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;

            var drive = (string)comboBoxDrives.SelectedItem;

            if(string.IsNullOrEmpty(textBoxFileName.Text))
            {
                var dr = saveFileDialog1.ShowDialog();
                if (dr != DialogResult.OK)
                    return;

                textBoxFileName.Text = saveFileDialog1.FileName;                
            }

            buttonRead.Enabled = false;
            buttonWrite.Enabled = false;
            buttonExit.Enabled = false;
            buttonCancel.Enabled = true;
            comboBoxDrives.Enabled = false;
            textBoxFileName.Enabled = false;
            buttonChooseFile.Enabled = false;
            try
            {
                ReadDrive(drive, textBoxFileName.Text);
            } catch(Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            buttonRead.Enabled = true;
            buttonWrite.Enabled = true;
            buttonExit.Enabled = true;
            buttonCancel.Enabled = false;
            comboBoxDrives.Enabled = true;
            textBoxFileName.Enabled = true;
            buttonChooseFile.Enabled = true;
        }

        private void ButtonWriteClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;
            
            var drive = (string)comboBoxDrives.SelectedItem;

            if (string.IsNullOrEmpty(textBoxFileName.Text))
            {
                var dr = openFileDialog1.ShowDialog();
                if (dr != DialogResult.OK)
                    return;

                textBoxFileName.Text = openFileDialog1.FileName;
            }

            buttonRead.Enabled = false;
            buttonWrite.Enabled = false;
            buttonExit.Enabled = false;
            buttonCancel.Enabled = true;
            comboBoxDrives.Enabled = false;
            textBoxFileName.Enabled = false;
            buttonChooseFile.Enabled = false;
            try
            {
                WriteDrive(drive, textBoxFileName.Text);
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            buttonRead.Enabled = true;
            buttonWrite.Enabled = true;
            buttonExit.Enabled = true;
            buttonCancel.Enabled = false;
            comboBoxDrives.Enabled = true;
            textBoxFileName.Enabled = true;
            buttonChooseFile.Enabled = true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private bool WriteDrive(string driveLetter, string fileName)
        {
            var success = false;
            int intOut;
            ulong driveSize = 0;

            _isCancelling = false;

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
                            toolStripStatusLabel1.Text = @"Failed to open device";
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            toolStripStatusLabel1.Text = @"Failed to lock device";
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            toolStripStatusLabel1.Text = @"Error dismounting volume: " + Marshal.GetHRForLastWin32Error();
                            NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        break;
                    }
                }
            }

            if (diskIndex < 0)
            {
                toolStripStatusLabel1.Text = @"Error: Couldn't map partition to physical drive";
                goto readfail3;
            }

            success = false;

            var physicalDrive = @"\\.\PhysicalDrive" + diskIndex;

            var diskHandle = NativeMethods.CreateFile(physicalDrive, NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                toolStripStatusLabel1.Text = @"Failed to open device: " + Marshal.GetHRForLastWin32Error();
                goto readfail3;
            }

            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)

            var geometrySize = Marshal.SizeOf(typeof(DiskGeometryEx));
            var geometryBlob = Marshal.AllocHGlobal(geometrySize);
            uint numBytesRead = 0;

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero,
                                                    0, geometryBlob, (uint)geometrySize, ref numBytesRead, IntPtr.Zero);
            if (!success)
            {
                toolStripStatusLabel1.Text = @"Failed get drive size";
                NativeMethods.CloseHandle(diskHandle);
                return false;
            }

            var geometry = (DiskGeometryEx)Marshal.PtrToStructure(geometryBlob, typeof(DiskGeometryEx));
            Marshal.FreeHGlobal(geometryBlob);

            driveSize = (ulong)(geometry.DiskSize);

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                toolStripStatusLabel1.Text = @"Failed to lock device";
                NativeMethods.CloseHandle(diskHandle);
                return false;
            }

            var buffer = new byte[MAX_BUFFER_SIZE];
            ulong offset = 0;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                using (var bw = new BinaryReader(fs))
                {
                    while (offset < driveSize && !_isCancelling)
                    {
#if true
                        // NOTE: Seem to need to keep setting the file pointer or ReadFile() returns 0 before the end of the drive
                        //       I think this is because of the sizing problem with CHS values that we deal with above.
                        // NOTE: (2) Now that I limit the buffer size on the last read we don't need to do this
                        var offsethigh = (int)(offset >> 32);
                        var offsetlow = (int) (offset & 0xFFFFFFFF);
                        var ptr = NativeMethods.SetFilePointer(diskHandle, offsetlow, ref offsethigh, EMoveMethod.Begin);
                        if (ptr == NativeMethods.INVALID_SET_FILE_POINTER)
                        {
                            toolStripStatusLabel1.Text = @"Error seeking on drive: " +
                                                         Marshal.GetHRForLastWin32Error();
                            goto readfail1;
                        }
#endif

                        var readBytes = bw.Read(buffer, 0, buffer.Length);

                        int wroteBytes;

                        if (NativeMethods.WriteFile(diskHandle, buffer, readBytes, out wroteBytes, IntPtr.Zero) < 0)
                        {
                            toolStripStatusLabel1.Text = @"Error writing data to drive: " +
                                                         Marshal.GetHRForLastWin32Error();
                            goto readfail1;
                        }

//                        if (wroteBytes != readBytes)
  //                      {
    //                        toolStripStatusLabel1.Text = @"Error writing data to drive - past EOF?";
      //                      continue;
        //                    goto readfail1;
          //              }

                        offset += (uint)wroteBytes;

                        var percentDone = (int)(100 * offset / driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset / tsElapsed.TotalSeconds;

                        progressBar1.Value = percentDone;
                        toolStripStatusLabel1.Text = @"Wrote " + percentDone + @"%, " + (offset / (1024 * 1024)) + @" MB / " +
                                                     (driveSize / (1024 * 1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec / (1024 * 1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss"));
                        Application.DoEvents();
                    }
                }
            }

            success = true;

        readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
        readfail2:
            NativeMethods.CloseHandle(diskHandle);
        readfail3:

        if (partitionHandle != null)
            NativeMethods.CloseHandle(partitionHandle);

            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if (_isCancelling)
                toolStripStatusLabel1.Text = "Cancelled";
            else if (success)
                toolStripStatusLabel1.Text = "All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss");
            progressBar1.Value = 0;
            return success;
        }

        /// <summary>
        /// Read data direct from drive to file
        /// </summary>
        /// <param name="driveLetter"></param>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private bool ReadDrive(string driveLetter, string fileName)
        {
            var success = false;
            int intOut;
            ulong driveSize = 0;

            _isCancelling = false;

            var dtStart = DateTime.Now;

            var diskIndex = -1;

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
                        diskIndex = (int)(UInt32)current["diskindex"];;

                        // Unmount partition (todo: Note that we currntly only handle unmounting of one partition, which is the usual case for SD Cards)
                        var partitionHandle = NativeMethods.CreateFile(@"\\.\" + driveLetter, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                        if (partitionHandle.IsInvalid)
                        {
                            toolStripStatusLabel1.Text = @"Failed to open device";
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            toolStripStatusLabel1.Text = @"Failed to lock device";
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        success = NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                        if (!success)
                        {
                            toolStripStatusLabel1.Text = @"Error dismounting volume: " + Marshal.GetHRForLastWin32Error();
                            NativeMethods.DeviceIoControl(partitionHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
                            NativeMethods.CloseHandle(partitionHandle);
                            return false;
                        }

                        NativeMethods.CloseHandle(partitionHandle);

                        break;
                    }
                }
            }

            if (diskIndex < 0)
            {
                toolStripStatusLabel1.Text = @"Error: Couldn't map partition to physical drive";
                goto readfail3;
            }

            success = false;

            var physicalDrive = @"\\.\PhysicalDrive" + diskIndex;
                        
            var diskHandle = NativeMethods.CreateFile(physicalDrive, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (diskHandle.IsInvalid)
            {
                toolStripStatusLabel1.Text = @"Failed to open device: " + Marshal.GetHRForLastWin32Error();
                goto readfail3;
            }

            // Get drive size (NOTE: that WMI and IOCTL_DISK_GET_DRIVE_GEOMETRY don't give us the right value so we do it this way)

            var geometrySize = Marshal.SizeOf(typeof (DiskGeometryEx));
            var geometryBlob = Marshal.AllocHGlobal(geometrySize);
            uint numBytesRead = 0;

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero,
                                                    0, geometryBlob, (uint) geometrySize, ref numBytesRead, IntPtr.Zero);
            if (!success)
            {
                toolStripStatusLabel1.Text = @"Failed get drive size";
                NativeMethods.CloseHandle(diskHandle);
                return false;
            }

            var geometry = (DiskGeometryEx)Marshal.PtrToStructure(geometryBlob, typeof(DiskGeometryEx));
            Marshal.FreeHGlobal(geometryBlob);

            driveSize = (ulong) (geometry.DiskSize);

            success = NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_LOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
            if (!success)
            {
                toolStripStatusLabel1.Text = @"Failed to lock device";
                NativeMethods.CloseHandle(diskHandle);
                return false;
            }

            var buffer = new byte[MAX_BUFFER_SIZE];
            ulong offset = 0;

            using (var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                using (var bw = new BinaryWriter(fs))
                {
                    while (offset < driveSize && !_isCancelling)
                    {
#if false
                        // NOTE: Seem to need to keep setting the file pointer or ReadFile() returns 0 before the end of the drive
                        //       I think this is because of the sizing problem with CHS values that we deal with above.
                        // NOTE: (2) Now that I limit the buffer size on the last read we don't need to do this
                        var offsethigh = (int)(offset >> 32);
                        var offsetlow = (int) (offset & 0xFFFFFFFF);
                        var ptr = NativeMethods.SetFilePointer(diskHandle, offsetlow, ref offsethigh, EMoveMethod.Begin);
                        if (ptr == NativeMethods.INVALID_SET_FILE_POINTER)
                        {
                            toolStripStatusLabel1.Text = @"Error seeking on drive: " +
                                                         Marshal.GetHRForLastWin32Error();
                            goto readfail1;
                        }
#endif

                        // NOTE: If we provide a buffer that extends past the end of the physical device ReadFile() doesn't
                        //       seem to do a partial read. Deal with this by reading the remaining bytes at the end of the
                        //       drive if necessary

                        var readMaxLength = (int) ( ( (driveSize - offset) < (ulong)buffer.Length) ? ( driveSize - offset) : (ulong)buffer.Length);

                        int readBytes;
                        if (NativeMethods.ReadFile(diskHandle, buffer, readMaxLength, out readBytes, IntPtr.Zero) < 0)
                        {
                            toolStripStatusLabel1.Text = @"Error reading data from drive: " +
                                                         Marshal.GetHRForLastWin32Error();
                            goto readfail1;
                        }


                        bw.Write(buffer, 0, readBytes);

                        if(readBytes == 0)
                        {
                            toolStripStatusLabel1.Text = @"Error reading data from drive - past EOF?";
                            goto readfail1;                            
                        }

                        offset += (uint)readBytes;

                        var percentDone = (int) (100*offset/driveSize);
                        var tsElapsed = DateTime.Now.Subtract(dtStart);
                        var bytesPerSec = offset/tsElapsed.TotalSeconds;

                        progressBar1.Value = percentDone;
                        toolStripStatusLabel1.Text = @"Read " + percentDone + @"%, " + (offset/(1024*1024)) + @" MB / " +
                                                     (driveSize/(1024*1024) + " MB, " +
                                                      string.Format("{0:F}", (bytesPerSec/(1024*1024))) + @" MB/sec, Elapsed time: " + tsElapsed.ToString(@"dd\.hh\:mm\:ss"));
                        Application.DoEvents();
                    }
                }
            }

            success = true;

readfail1:
            NativeMethods.DeviceIoControl(diskHandle, NativeMethods.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out intOut, IntPtr.Zero);
readfail2:
            NativeMethods.CloseHandle(diskHandle);
readfail3:
            var tstotalTime = DateTime.Now.Subtract(dtStart);

            if(_isCancelling)
                toolStripStatusLabel1.Text = "Cancelled";
            else if (success)
                toolStripStatusLabel1.Text = "All Done - Elapsed time " + tstotalTime.ToString(@"dd\.hh\:mm\:ss");
            progressBar1.Value = 0;
            return success;
        }

        private void MainFormFormClosing(object sender, FormClosingEventArgs e)
        {
            var key = Registry.LocalMachine.CreateSubKey("SOFTWARE\\Dynamic Devices Ltd\\DiskImager");
            if (key != null)
            {
                key.SetValue("FileName", textBoxFileName.Text);
                key.Close();
            }

            _watcher.Stop();
        }

        private void ButtonCancelClick(object sender, EventArgs e)
        {
            _isCancelling = true;
        }

        private void ComboBoxDrivesSelectedIndexChanged(object sender, EventArgs e)
        {
            labelSize.Text = "";

            if(comboBoxDrives.SelectedIndex >= 0)
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    // Only display removable drives
                    if (drive.DriveType == DriveType.Removable)
                    {
                        if (drive.Name == comboBoxDrives.SelectedItem + "\\")
                            labelSize.Text = @"Size: " + (drive.TotalSize/(1024*1024)) + @" MB";
                    }
                }
            }
        }
    }
}
