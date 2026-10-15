using System.Runtime.InteropServices;

namespace OmniPanel.Monitoring.Providers.Hwinfo;

/// <summary>
/// Packed HWiNFO shared-memory wire format. Layout matches the public
/// <c>Global\HWiNFO_SENS_SM2</c> contract; strides always come from the header
/// so older revisions keep parsing.
/// </summary>
internal static class HwinfoSharedMemoryFormat
{
    /// <summary>Mapping name HWiNFO publishes when shared-memory support is on.</summary>
    public const string MappingName = @"Global\HWiNFO_SENS_SM2";

    /// <summary>Mutex HWiNFO takes around updates; optional for readers.</summary>
    public const string MutexName = @"Global\HWiNFO_SM2_MUTEX";

    /// <summary>Little-endian ASCII <c>SiWH</c> ("HWiS" as a DWORD).</summary>
    public const uint LiveSignature = 0x53695748;

    /// <summary>Signature written when HWiNFO is shutting the mapping down.</summary>
    public const uint DeadSignature = 0x44414544; // 'DEAD'

    /// <summary>Minimum header bytes before revision-1's polling-period field.</summary>
    public const int HeaderSize = 44;

    /// <summary>Reading-type codes HWiNFO stores in each reading element.</summary>
    public enum ReadingType : uint
    {
        None = 0,
        Temperature = 1,
        Voltage = 2,
        Fan = 3,
        Current = 4,
        Power = 5,
        Clock = 6,
        Usage = 7,
        Other = 8,
    }

    /// <summary>Header at offset 0 of the mapping.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Header
    {
        public uint Signature;
        public uint Version;
        public uint Revision;
        public long PollTime;
        public uint OffsetOfSensorSection;
        public uint SizeOfSensorElement;
        public uint NumSensorElements;
        public uint OffsetOfReadingSection;
        public uint SizeOfReadingElement;
        public uint NumReadingElements;
    }
}
