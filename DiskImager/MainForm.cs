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
        #region Fields

        private readonly ManagementEventWatcher _watcher = new ManagementEventWatcher();

        private readonly Disk _disk;

        private EnumCompressionType _eCompType;

        #endregion

        #region Constructor

        public MainForm()
        {
            InitializeComponent();

            toolStripStatusLabel1.Text = @"OK";

            saveFileDialog1.OverwritePrompt = false;
            saveFileDialog1.Filter = @"Image Files (*.img,*.bin,*.sdcard)|*.img;*.bin;*.sdcard|Compressed Files (*.zip,*.gz,*tgz)|*.zip;*.gz;*.tgz|All files (*.*)|*.*";

            // Set version into title
            var version = Assembly.GetEntryAssembly().GetName().Version;
            Text += @" v" + version;

            // Set app icon
            Icon = Utility.GetAppIcon();

            PopulateDrives();
            if (comboBoxDrives.Items.Count > 0)
                EnableButtons();
            else
                DisableButtons(false);

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

                Globals.CompressionLevel = (int)key.GetValue("CompressionLevel", Globals.CompressionLevel);
                Globals.MaxBufferSize = (int)key.GetValue("MaxBufferSize", Globals.MaxBufferSize);

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

        #endregion

        public override sealed string Text
        {
            get { return base.Text; }
            set { base.Text = value; }
        }

        #region Disk access event handlers

        /// <summary>
        /// Called to update progress bar as we read/write disk
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="progressPercentage"></param>
        void _disk_OnProgress(object sender, int progressPercentage)
        {
            progressBar1.Value = progressPercentage;
            Application.DoEvents();
        }

        /// <summary>
        /// Called to display/log messages from disk handling
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="message"></param>
        void _disk_OnLogMsg(object sender, string message)
        {
            toolStripStatusLabel1.Text = message;
            Application.DoEvents();
        }

        #endregion

        #region UI Handling

        /// <summary>
        /// Close the application
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonExitClick(object sender, EventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Select a file for read/write from/to removable media
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonChooseFileClick(object sender, EventArgs e)
        {

            ChooseFile();
        }

        /// <summary>
        /// Read from removable media to file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonReadClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;

            var drive = (string)comboBoxDrives.SelectedItem;

            if(string.IsNullOrEmpty(textBoxFileName.Text))
                ChooseFile();

            DisableButtons(true);

            try
            {
                _disk.ReadDrive(drive, textBoxFileName.Text, _eCompType);
            } catch(Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            EnableButtons();
        }

        /// <summary>
        /// Write to removable media from file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonWriteClick(object sender, EventArgs e)
        {
            if (comboBoxDrives.SelectedIndex < 0)
                return;
            
            var drive = (string)comboBoxDrives.SelectedItem;

            if (string.IsNullOrEmpty(textBoxFileName.Text))
                ChooseFile();

            DisableButtons(true);

            try
            {
                _disk.WriteDrive(drive, textBoxFileName.Text, _eCompType);
            }
            catch (Exception ex)
            {
                toolStripStatusLabel1.Text = ex.Message;
            }

            EnableButtons();
        }

        /// <summary>
        /// Called to persist registry values on closure so we can remember things like last file used
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        /// <summary>
        /// Cancels an ongoing read/write
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ButtonCancelClick(object sender, EventArgs e)
        {
            _disk.IsCancelling = true;
        }

        private void RadioButtonCompZipCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompTgzCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompGzCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        private void RadioButtonCompNoneCheckedChanged(object sender, EventArgs e)
        {
            UpdateFileNameText();
        }

        #endregion

        #region Implementation

        private void UpdateFileNameText()
        {
            var text = textBoxFileName.Text;
            text = text.Replace(".tar.gz", "");
            text = text.Replace(".tgz", "");
            text = text.Replace(".tar", "");
            text = text.Replace(".gzip", "");
            text = text.Replace(".gz", "");
            text = text.Replace(".zip", "");

            if (radioButtonCompNone.Checked)
            {
                textBoxFileName.Text = text;
            } else if(radioButtonCompZip.Checked)
            {
                text += ".zip";
                textBoxFileName.Text = text;                
            } else if(radioButtonCompTgz.Checked)
            {
                text += ".tgz";
                textBoxFileName.Text = text;
            }
            else if (radioButtonCompGz.Checked)
            {
                text += ".gz";
                textBoxFileName.Text = text;
            }
        }

        /// <summary>
        /// Select the file for read/write and setup defaults for whether we're using compression based on extension
        /// </summary>
        private void ChooseFile()
        {
            var dr = saveFileDialog1.ShowDialog();
            if (dr != DialogResult.OK)
                return;
            
            textBoxFileName.Text = saveFileDialog1.FileName;
            TextBoxFileNameTextChanged(this, null);
        }

        private void TextBoxFileNameTextChanged(object sender, EventArgs e)
        {
            if (textBoxFileName.Text.ToLower().EndsWith(".tar.gz") || textBoxFileName.Text.ToLower().EndsWith(".tgz"))
                radioButtonCompTgz.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".gz"))
                radioButtonCompGz.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".zip"))
                radioButtonCompZip.Checked = true;
            else if (textBoxFileName.Text.ToLower().EndsWith(".img") || textBoxFileName.Text.ToLower().EndsWith(".bin") || textBoxFileName.Text.ToLower().EndsWith(".sdcard"))
                radioButtonCompNone.Checked = true;

            if (radioButtonCompNone.Checked)
                _eCompType = EnumCompressionType.None;
            else if (radioButtonCompTgz.Checked)
                _eCompType = EnumCompressionType.Targzip;
            else if (radioButtonCompGz.Checked)
                _eCompType = EnumCompressionType.Gzip;
            else if (radioButtonCompZip.Checked)
                _eCompType = EnumCompressionType.Zip;
        }

        /// <summary>
        /// Load in the drives
        /// </summary>
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

        /// <summary>
        /// Callback when removable media is inserted or removed, repopulates the drive list
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void WatcherEventArrived(object sender, EventArgs e)
        {
            if(InvokeRequired)
            {
                Invoke(new EventHandler(WatcherEventArrived));
                return;
            }

            PopulateDrives();

            if (comboBoxDrives.Items.Count > 0)
                EnableButtons();
            else
                DisableButtons(false);
        }

        /// <summary>
        /// Updates UI to disable buttons
        /// </summary>
        /// <param name="running">Whether read/write process is running</param>
        private void DisableButtons(bool running)
        {
            buttonRead.Enabled = false;
            buttonWrite.Enabled = false;
            buttonExit.Enabled = !running;
            buttonCancel.Enabled = running;
            comboBoxDrives.Enabled = false;
            textBoxFileName.Enabled = false;
            buttonChooseFile.Enabled = false;
            groupBoxCompression.Enabled = false;
        }

        /// <summary>
        /// Updates UI to enable buttons
        /// </summary>
        private void EnableButtons()
        {
            buttonRead.Enabled = true;
            buttonWrite.Enabled = true;
            buttonExit.Enabled = true;
            buttonCancel.Enabled = false;
            comboBoxDrives.Enabled = true;
            textBoxFileName.Enabled = true;
            buttonChooseFile.Enabled = true;
            groupBoxCompression.Enabled = true;
        }

        #endregion

    }
}
