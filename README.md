DiskImager
==========

A Windows Disk Imager. A C#.NET utility for reading / writing SD cards and USB devices

License: This software utility is released under GPLv3.

The current release can be downloaded here 

http://www.dynamicdevices.co.uk/downloads/DiskImager.Installer.msi

(Please feed back any platform testing you do, or any issues you encounter. Thanks.)

This utility is a C#.NET implementation, and adds a couple of features I wanted to see:

- reads/writes images to/from compressed file formats: ZIP, TGZ, GZ

- remembers the last file you read/wrote 

- provides more file filters within file dialog for typical image files (.img, .bin, .sdcard)

- it also *might* be slightly faster when dealing with uncompressed read/write

*NOTE This application is under development and could possibly cause damage to your computer drive(s). We cannot take responsibility for any damage caused or losses incurred through use of this utility. Use at own risk!*

Credits: Inspired by the excellent Win32DiskImager.

ChangeLog
=========

1.1.1	02/03/14	AJL		Minor fix to error message when source file not available

1.1.0	12/05/14	AJL		Updated to use latest SharpZipLib as we were encountering (de-)compression errors with the previous version
							Added the option to truncate the read image based on the partition sizes found in the master boot record on the disk/stick
							Improved logging of sizes read and written

1.0.3	30/04/14	AJL		Added warning dialog box when there's a write error 

1.0.2	09/11/13	AJL		Added support for reading and writing directly to compressed formats: .zip, .tgz, .gz

	Testing - Windows 8.1 Professional

1.0.1	08/11/13	AJL		Refactoring for cleanup. Fixed issue with SEH exception due to SafeHandle disposal

	Testing - Windows 8.1 Professional

1.0.0	08/11/13	AJL		Initial Commit. Reads and Writes SD cards

	Testing - Windows 8.1 Professional

Contact
=======

Alex J Lennon - ajlennon@dynamicdevices.co.uk