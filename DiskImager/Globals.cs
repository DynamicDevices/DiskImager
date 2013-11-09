namespace DynamicDevices.DiskWriter
{
    internal class Globals
    {
        private const int MAX_BUFFER_SIZE = 1 * 1024 * 1024;
        private const int DEFAULT_COMPRESSION_LEVEL = 3;

        private static int _maxBufferSize = MAX_BUFFER_SIZE;
        private static int _compressionLevel = DEFAULT_COMPRESSION_LEVEL;

        public static int MaxBufferSize { get { return _maxBufferSize; } set { _maxBufferSize = value;  } }

        public static int CompressionLevel { get { return _compressionLevel; } set { _compressionLevel = value;  } }
    }
}
