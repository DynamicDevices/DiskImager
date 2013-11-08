using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DynamicDevices.DiskWriter
{
    internal class Globals
    {
        private const int MAX_BUFFER_SIZE = 1 * 1024 * 1024;

        private static int _maxBufferSize = MAX_BUFFER_SIZE;

        public static int MaxBufferSize { get { return _maxBufferSize; } set { _maxBufferSize = value;  } }
    }
}
