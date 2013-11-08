using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Management;
using Microsoft.Win32;

namespace DynamicDevices.DiskWriter
{
    public partial class MainForm : Form
    {
        private readonly ManagementEventWatcher _watcher = new ManagementEventWatcher();

        private readonly Disk _disk;

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
                        if((string) cbDrive == drive)
                        {
                            comboBoxDrives.SelectedItem = cbDrive;
                        }
                    }
                }
                key.Close();
            }

            // Create disk object for media accesses
            _disk = new Disk();
            _disk.OnLogMsg += _disk_OnLogMsg;
            _disk.OnProgress += _disk_OnProgress;
            
            // Detect insertions / removals
            var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
            _watcher.EventArrived += WatcherEventArrived;
            _watcher.Query = query;
            _watcher.Start();        
        }

        void _disk_OnProgress(object sender, int progressPercentage)
        {
            progressBar1.Value = progressPercentage;
            Application.DoEvents();
        }

        void _disk_OnLogMsg(object sender, string message)
        {
            toolStripStatusLabel1.Text = message;
            Application.DoEvents();
        }

        public override sealed string Text
        {
            get { return base.Text; }
            set { base.Text = value; }
        }

        #region UI Handling

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

            DisableButtons();

            try
            {
                _disk.ReadDrive(drive, textBoxFileName.Text);
            } catch(Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            EnableButtons();
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

            DisableButtons();

            try
            {
                _disk.WriteDrive(drive, textBoxFileName.Text);
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            EnableButtons();
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
            _disk.IsCancelling = true;
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

        #endregion

        #region Implementation

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
                    comboBoxDrives.Items.Add(drive.Name.TrimEnd(new[] { '\\' }));
                }
            }

            if (comboBoxDrives.Items.Count > 0)
                comboBoxDrives.SelectedIndex = 0;
        }

        void WatcherEventArrived(object sender, EventArrivedEventArgs e)
        {
            PopulateDrives();
        }

        private void DisableButtons()
        {
            buttonRead.Enabled = false;
            buttonWrite.Enabled = false;
            buttonExit.Enabled = false;
            buttonCancel.Enabled = true;
            comboBoxDrives.Enabled = false;
            textBoxFileName.Enabled = false;
            buttonChooseFile.Enabled = false;            
        }

        private void EnableButtons()
        {
            buttonRead.Enabled = true;
            buttonWrite.Enabled = true;
            buttonExit.Enabled = true;
            buttonCancel.Enabled = false;
            comboBoxDrives.Enabled = true;
            textBoxFileName.Enabled = true;
            buttonChooseFile.Enabled = true;
        }

        #endregion

    }
}
