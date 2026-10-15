using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace Panelith.Media;

/// <summary>
/// A looping clip baked for one panel: JPEG frames at a fixed rate and size.
/// </summary>
/// <remarks>
/// <para>
/// Layout, little-endian: the 8-byte magic <c>PTCLIP01</c>, width and height
/// (uint16), frames per second (uint16), reserved (uint16), frame count (uint32),
/// the offset of the frame table (uint64), then the JPEG frames, then the table:
/// frame count + 1 absolute uint64 offsets. Frame <c>i</c> spans offsets
/// <c>i</c> to <c>i + 1</c>. The table goes last so a bake can stream frames
/// without knowing how many there will be.
/// </para>
/// <para>
/// Clips arrive in theme folders other people share, so every field is checked
/// against <see cref="ClipLimits"/> before anything is allocated.
/// </para>
/// </remarks>
public static class ClipFile
{
    /// <summary>The conventional file extension.</summary>
    public const string Extension = ".ptclip";

    internal const int HeaderBytes = 28;

    private static ReadOnlySpan<byte> Magic => "PTCLIP01"u8;

    /// <summary>Writes a clip. Frames must already be JPEG-encoded at <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static void Write(Stream output, int width, int height, int fps, IReadOnlyList<ReadOnlyMemory<byte>> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using var writer = new ClipWriter(output, width, height, fps);
        foreach (var frame in frames)
        {
            writer.Add(frame.Span);
        }

        writer.Complete();
    }

    /// <summary>Reads and validates a clip's header and frame table.</summary>
    public static ClipInfo ReadInfo(string path)
    {
        using var reader = ClipReader.Open(path);
        return reader.Info;
    }

    internal static void WriteHeader(Span<byte> header, int width, int height, int fps, int frameCount, long tableOffset)
    {
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)fps);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)frameCount);
        BinaryPrimitives.WriteUInt64LittleEndian(header[20..], (ulong)tableOffset);
    }

    internal static (ClipInfo Info, ulong TableOffset) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderBytes || !header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is not a Panelith clip.");
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
        var fps = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var table = BinaryPrimitives.ReadUInt64LittleEndian(header[20..]);
        if (count > ClipLimits.MaxFrames)
        {
            throw new InvalidDataException($"The clip has {count} frames; the most allowed is {ClipLimits.MaxFrames}.");
        }

        ClipLimits.Check(width, height, fps, (int)count);
        return (new ClipInfo(width, height, fps, (int)count), table);
    }
}

/// <summary>A clip's size, rate and length.</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Fps">Frames per second.</param>
/// <param name="FrameCount">Number of frames in one loop.</param>
public readonly record struct ClipInfo(int Width, int Height, int Fps, int FrameCount)
{
    /// <summary>Length of one loop.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / (double)Fps);

    /// <summary>
    /// The frame shown <paramref name="elapsed"/> after the clip started, looping,
    /// at <paramref name="speed"/> times normal speed.
    /// </summary>
    public int FrameAt(TimeSpan elapsed, double speed = 1d)
    {
        if (FrameCount <= 1 || !double.IsFinite(speed) || speed == 0d)
        {
            return 0;
        }

        var index = Math.Floor(elapsed.TotalSeconds * Fps * speed) % FrameCount;
        return (int)(index < 0 ? index + FrameCount : index);
    }
}

/// <summary>The bounds every clip must stay within.</summary>
public static class ClipLimits
{
    /// <summary>Largest frame width or height.</summary>
    public const int MaxDimension = 4096;

    /// <summary>Highest frame rate.</summary>
    public const int MaxFps = 60;

    /// <summary>Most frames in one clip: ten minutes at 15 fps.</summary>
    public const int MaxFrames = 9000;

    /// <summary>Largest single encoded frame.</summary>
    public const long MaxFrameBytes = 8L * 1024 * 1024;

    /// <summary>Largest clip file.</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Throws <see cref="InvalidDataException"/> when a clip's size, rate or length is out of bounds.</summary>
    public static void Check(int width, int height, int fps, int frameCount)
    {
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension)
        {
            throw new InvalidDataException($"A clip frame of {width}x{height} is outside 1-{MaxDimension} pixels.");
        }

        if (fps is < 1 or > MaxFps)
        {
            throw new InvalidDataException($"A clip rate of {fps} fps is outside 1-{MaxFps}.");
        }

        if (frameCount is < 1 or > MaxFrames)
        {
            throw new InvalidDataException($"A clip of {frameCount} frames is outside 1-{MaxFrames}.");
        }
    }
}

/// <summary>
/// Writes a clip frame by frame, so a long bake never holds every frame in memory.
/// Call <see cref="Complete"/> to finish it; a clip that is not completed is invalid.
/// </summary>
public sealed class ClipWriter : IDisposable
{
    private readonly Stream _output;
    private readonly long _start;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly List<long> _offsets = [];
    private long _position;
    private bool _completed;

    /// <summary>Starts a clip at the current position of a seekable <paramref name="output"/>.</summary>
    public ClipWriter(Stream output, int width, int height, int fps)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite)
        {
            throw new ArgumentException("The clip stream must be writable and seekable.", nameof(output));
        }

        ClipLimits.Check(width, height, fps, 1);
        _output = output;
        _start = output.Position;
        _width = width;
        _height = height;
        _fps = fps;

        // A header with no table marks the clip unfinished until Complete rewrites it.
        Span<byte> header = stackalloc byte[ClipFile.HeaderBytes];
        ClipFile.WriteHeader(header, width, height, fps, 0, 0);
        _output.Write(header);
        _position = ClipFile.HeaderBytes;
    }

    /// <summary>Frames written so far.</summary>
    public int Count => _offsets.Count;

    /// <summary>Appends one JPEG frame.</summary>
    public void Add(ReadOnlySpan<byte> jpeg)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (jpeg.Length == 0 || jpeg.Length > ClipLimits.MaxFrameBytes)
        {
            throw new InvalidDataException($"A clip frame of {jpeg.Length} bytes is outside 1-{ClipLimits.MaxFrameBytes} bytes.");
        }

        if (_offsets.Count >= ClipLimits.MaxFrames)
        {
            throw new InvalidDataException($"A clip holds at most {ClipLimits.MaxFrames} frames.");
        }

        var tableBytes = (long)(_offsets.Count + 2) * sizeof(ulong);
        if (_position + jpeg.Length + tableBytes > ClipLimits.MaxFileBytes)
        {
            throw new InvalidDataException("The clip would be larger than the 2 GB limit.");
        }

        _offsets.Add(_position);
        _output.Write(jpeg);
        _position += jpeg.Length;
    }

    /// <summary>Writes the frame table and the final header.</summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_offsets.Count == 0)
        {
            throw new InvalidDataException("A clip needs at least one frame.");
        }

        _completed = true;
        var table = new byte[(_offsets.Count + 1) * sizeof(ulong)];
        for (var i = 0; i < _offsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(i * 8), (ulong)_offsets[i]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(_offsets.Count * 8), (ulong)_position);
        _output.Write(table);
        var end = _output.Position;

        Span<byte> header = stackalloc byte[ClipFile.HeaderBytes];
        ClipFile.WriteHeader(header, _width, _height, _fps, _offsets.Count, _position);
        _output.Position = _start;
        _output.Write(header);
        _output.Position = end;
        _output.Flush();
    }

    /// <inheritdoc />
    public void Dispose() => _completed = true;
}

/// <summary>
/// Reads frames from a clip file through a memory map, so playing a long clip
/// costs one frame of memory rather than the whole file.
/// </summary>
public sealed class ClipReader : IDisposable
{
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long[] _offsets;

    private ClipReader(MemoryMappedFile map, MemoryMappedViewAccessor view, ClipInfo info, long[] offsets)
    {
        _map = map;
        _view = view;
        Info = info;
        _offsets = offsets;
    }

    /// <summary>The clip's size, rate and length.</summary>
    public ClipInfo Info { get; }

    /// <summary>Opens and validates a clip. Links are refused, as for every theme asset.</summary>
    public static ClipReader Open(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The clip does not exist.", path);
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The clip is a link, which themes may not use.");
        }

        var length = file.Length;
        if (length < ClipFile.HeaderBytes || length > ClipLimits.MaxFileBytes)
        {
            throw new InvalidDataException("The clip is cut short or larger than the 2 GB limit.");
        }

        var map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        try
        {
            view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var header = new byte[ClipFile.HeaderBytes];
            view.ReadArray(0, header, 0, header.Length);
            var (info, tableOffset) = ClipFile.ParseHeader(header);

            var tableBytes = (long)(info.FrameCount + 1) * sizeof(ulong);
            if (tableOffset < ClipFile.HeaderBytes || tableOffset > (ulong)(length - tableBytes))
            {
                throw new InvalidDataException("The clip's frame table is missing or cut short.");
            }

            var raw = new byte[tableBytes];
            view.ReadArray((long)tableOffset, raw, 0, raw.Length);
            var offsets = new long[info.FrameCount + 1];
            long previous = ClipFile.HeaderBytes;
            for (var i = 0; i < offsets.Length; i++)
            {
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(i * 8));
                if (offset > tableOffset || (long)offset < previous || (i > 0 && (long)offset == previous))
                {
                    throw new InvalidDataException($"Frame {Math.Max(0, i - 1)} of the clip is empty or points outside the frames.");
                }

                if (i > 0 && (long)offset - previous > ClipLimits.MaxFrameBytes)
                {
                    throw new InvalidDataException($"Frame {i - 1} of the clip is larger than the frame limit.");
                }

                offsets[i] = (long)offset;
                previous = (long)offset;
            }

            return new ClipReader(map, view, info, offsets);
        }
        catch
        {
            view?.Dispose();
            map.Dispose();
            throw;
        }
    }

    /// <summary>The encoded size of frame <paramref name="index"/>.</summary>
    public int FrameLength(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Info.FrameCount);
        return (int)(_offsets[index + 1] - _offsets[index]);
    }

    /// <summary>Copies frame <paramref name="index"/>'s JPEG bytes into <paramref name="destination"/>, returning the length.</summary>
    public int ReadFrame(int index, byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var count = FrameLength(index);
        if (destination.Length < count)
        {
            throw new ArgumentException("The buffer is too small for the frame.", nameof(destination));
        }

        _view.ReadArray(_offsets[index], destination, 0, count);
        return count;
    }

    /// <summary>Frame <paramref name="index"/>'s JPEG bytes in a new array.</summary>
    public byte[] ReadFrame(int index)
    {
        var bytes = new byte[FrameLength(index)];
        ReadFrame(index, bytes);
        return bytes;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _view.Dispose();
        _map.Dispose();
    }
}
