using System.Runtime.InteropServices;

namespace DynamicDevices.DiskWriter
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskGeometry
    {
        public long Cylinders;
        public int MediaType;
        public int TracksPerCylinder;
        public int SectorsPerTrack;
        public int BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskGeometryEx
    {
        public DiskGeometry Geometry;
        public long DiskSize;
        public byte Data;
    }
}
