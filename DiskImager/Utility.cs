using System;
using System.Drawing;
using System.Reflection;

namespace DynamicDevices.DiskWriter
{
    internal class Utility
    {
        public static Icon GetAppIcon()
        {
            var fileName = Assembly.GetEntryAssembly().Location;

            var hLibrary = NativeMethods.LoadLibrary(fileName);
            if (!hLibrary.Equals(IntPtr.Zero))
            {
                var hIcon = NativeMethods.LoadIcon(hLibrary, "#32512");
                if (!hIcon.Equals(IntPtr.Zero))
                    return Icon.FromHandle(hIcon);
            }
            return null; //no icon was retrieved
        }
    }
}
