using System.Buffers.Binary;
using System.Text;

namespace Panelith.Media;

/// <summary>
/// Rewrites the video track of a fragmented MP4 (samples in <c>moof</c>/<c>mdat</c>
/// pairs, as YouTube, OBS and DASH produce) into a plain MP4 with one sample
/// table, which Windows Media Foundation decodes. The pictures are copied, not
/// re-encoded.
/// </summary>
public static class Mp4Defragmenter
{
    private const int MaxMetadataBox = 64 * 1024 * 1024;
    private const int MaxSamples = 2_000_000;

    /// <summary>Whether <paramref name="path"/> is an MP4 whose samples are in fragments.</summary>
    public static bool IsFragmented(string path)
    {
        using var stream = OpenRead(path);
        var sawMoov = false;
        foreach (var box in TopLevel(stream))
        {
            if (box.Type == "moov")
            {
                sawMoov = true;
            }
            else if (box.Type == "moof")
            {
                return sawMoov;
            }
            else if (box.Type == "mdat" && sawMoov)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>Writes the plain MP4 to <paramref name="output"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not a fragmented MP4 with a video track.</exception>
    public static void Defragment(string input, string output, CancellationToken cancellationToken = default)
    {
        using var source = OpenRead(input);
        byte[]? ftyp = null;
        byte[]? moov = null;
        var samples = new List<Sample>();
        Track? track = null;

        foreach (var box in TopLevel(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (box.Type)
            {
                case "ftyp":
                    ftyp = ReadWhole(source, box);
                    break;
                case "moov":
                    moov = ReadWhole(source, box);
                    track = Track.Parse(moov);
                    break;
                case "moof" when track is not null:
                    ReadFragment(ReadWhole(source, box), box.Start, track, samples);
                    break;
            }
        }

        if (moov is null || track is null)
        {
            throw new InvalidDataException("The MP4 has no video track.");
        }

        if (samples.Count == 0)
        {
            throw new InvalidDataException("The MP4 has no pictures.");
        }

        var dataLength = samples.Sum(s => (long)s.Size);
        var header = ftyp ?? Box("ftyp", Encoding.ASCII.GetBytes("isom\0\0\u0002\0isomiso2avc1mp41"));
        var probe = BuildMoov(moov, track, samples, firstOffset: 0);
        var mdatHeader = dataLength + 8 > uint.MaxValue ? 16 : 8;
        var firstOffset = header.Length + probe.Length + mdatHeader;
        var finalMoov = BuildMoov(moov, track, samples, firstOffset);

        var partial = output + ".partial";
        try
        {
            using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                target.Write(header);
                target.Write(finalMoov);
                Span<byte> boxHeader = stackalloc byte[16];
                if (mdatHeader == 16)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(boxHeader, 1);
                    Encoding.ASCII.GetBytes("mdat", boxHeader[4..]);
                    BinaryPrimitives.WriteUInt64BigEndian(boxHeader[8..], (ulong)(dataLength + 16));
                    target.Write(boxHeader);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(boxHeader, (uint)(dataLength + 8));
                    Encoding.ASCII.GetBytes("mdat", boxHeader[4..]);
                    target.Write(boxHeader[..8]);
                }

                var buffer = new byte[1 << 20];
                foreach (var sample in samples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    source.Position = sample.Offset;
                    var left = sample.Size;
                    while (left > 0)
                    {
                        var read = source.Read(buffer, 0, Math.Min(buffer.Length, left));
                        if (read <= 0)
                        {
                            throw new InvalidDataException("The MP4 ends in the middle of a picture.");
                        }

                        target.Write(buffer, 0, read);
                        left -= read;
                    }
                }
            }

            File.Move(partial, output, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    private static IEnumerable<BoxInfo> TopLevel(Stream stream)
    {
        var position = 0L;
        Span<byte> header = stackalloc byte[16];
        var boxes = new List<BoxInfo>();
        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            stream.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header.Slice(4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                stream.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = stream.Length - position;
            }

            if (size < headerLength || position + size > stream.Length)
            {
                break;
            }

            boxes.Add(new BoxInfo(type, position, size, headerLength));
            position += size;
        }

        return boxes;
    }

    private static byte[] ReadWhole(Stream stream, BoxInfo box)
    {
        if (box.Size > MaxMetadataBox)
        {
            throw new InvalidDataException($"The MP4's {box.Type} box is too large.");
        }

        var bytes = new byte[box.Size];
        stream.Position = box.Start;
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The child boxes of a box held in memory.</summary>
    private static List<(string Type, int Start, int Length, int Header)> Children(ReadOnlySpan<byte> data, int start, int end)
    {
        var children = new List<(string, int, int, int)>();
        var position = start;
        while (position + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            if (size < header || position + size > end)
            {
                throw new InvalidDataException("The MP4 has a damaged box.");
            }

            children.Add((Encoding.ASCII.GetString(data.Slice(position + 4, 4)), position, (int)size, header));
            position += (int)size;
        }

        return children;
    }

    private static (int Start, int Length, int Header)? Find(ReadOnlySpan<byte> data, int start, int end, string type)
    {
        foreach (var child in Children(data, start, end))
        {
            if (child.Type == type)
            {
                return (child.Start, child.Length, child.Header);
            }
        }

        return null;
    }

    private static void ReadFragment(byte[] moof, long moofStart, Track track, List<Sample> samples)
    {
        foreach (var traf in Children(moof, 8, moof.Length).Where(c => c.Type == "traf"))
        {
            var trafEnd = traf.Start + traf.Length;
            var tfhd = Find(moof, traf.Start + traf.Header, trafEnd, "tfhd") ?? throw new InvalidDataException("The MP4 has a fragment without a track header.");
            var p = tfhd.Start + tfhd.Header;
            var flags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p)) & 0xFFFFFF;
            var trackId = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p + 4));
            if (trackId != track.Id)
            {
                continue;
            }

            p += 8;
            var baseOffset = moofStart;
            if ((flags & 0x1) != 0)
            {
                baseOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(moof.AsSpan(p));
                p += 8;
            }

            if ((flags & 0x2) != 0)
            {
                p += 4;
            }

            var defaultDuration = track.DefaultDuration;
            var defaultSize = track.DefaultSize;
            var defaultFlags = track.DefaultFlags;
            if ((flags & 0x8) != 0)
            {
                defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p));
                p += 4;
            }

            if ((flags & 0x10) != 0)
            {
                defaultSize = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p));
                p += 4;
            }

            if ((flags & 0x20) != 0)
            {
                defaultFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(p));
            }

            var dataEnd = baseOffset;
            foreach (var trun in Children(moof, traf.Start + traf.Header, trafEnd).Where(c => c.Type == "trun"))
            {
                var q = trun.Start + trun.Header;
                var version = moof[q];
                var runFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q)) & 0xFFFFFF;
                var count = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q + 4));
                q += 8;
                if (samples.Count + count > MaxSamples)
                {
                    throw new InvalidDataException("The MP4 has too many pictures.");
                }

                var offset = dataEnd;
                if ((runFlags & 0x1) != 0)
                {
                    offset = baseOffset + BinaryPrimitives.ReadInt32BigEndian(moof.AsSpan(q));
                    q += 4;
                }

                uint? firstFlags = null;
                if ((runFlags & 0x4) != 0)
                {
                    firstFlags = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q));
                    q += 4;
                }

                for (var i = 0; i < count; i++)
                {
                    var duration = defaultDuration;
                    var size = defaultSize;
                    var sampleFlags = i == 0 && firstFlags is { } first ? first : defaultFlags;
                    var compositionOffset = 0;
                    if ((runFlags & 0x100) != 0)
                    {
                        duration = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q));
                        q += 4;
                    }

                    if ((runFlags & 0x200) != 0)
                    {
                        size = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q));
                        q += 4;
                    }

                    if ((runFlags & 0x400) != 0)
                    {
                        var perSample = BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q));
                        q += 4;
                        if (!(i == 0 && firstFlags is not null))
                        {
                            sampleFlags = perSample;
                        }
                    }

                    if ((runFlags & 0x800) != 0)
                    {
                        compositionOffset = version == 0
                            ? (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(moof.AsSpan(q)))
                            : BinaryPrimitives.ReadInt32BigEndian(moof.AsSpan(q));
                        q += 4;
                    }

                    if (size == 0 || size > ClipLimits.MaxFrameBytes * 8)
                    {
                        throw new InvalidDataException("The MP4 has a picture of an impossible size.");
                    }

                    var isSync = (sampleFlags & 0x10000) == 0;
                    samples.Add(new Sample(offset, (int)size, duration, compositionOffset, isSync));
                    offset += size;
                }

                dataEnd = offset;
            }
        }
    }

    private static byte[] BuildMoov(byte[] moov, Track track, List<Sample> samples, long firstOffset)
    {
        var mediaDuration = samples.Sum(s => (long)s.Duration);
        var movieDuration = track.MediaTimescale == 0 ? mediaDuration : mediaDuration * track.MovieTimescale / track.MediaTimescale;

        var stbl = Box("stbl", Concat(
            Slice(moov, track.Stsd),
            SampleTimes(samples),
            CompositionOffsets(samples),
            SyncSamples(samples),
            FullBox("stsc", 0, Write32(1), Write32(1), Write32(1), Write32(1)),
            SampleSizes(samples),
            ChunkOffsets(samples, firstOffset)));

        var minfChildren = Children(moov, track.Minf.Start + track.Minf.Header, track.Minf.Start + track.Minf.Length)
            .Where(c => c.Type != "stbl")
            .Select(c => moov.AsSpan(c.Start, c.Length).ToArray())
            .Append(stbl);
        var minf = Box("minf", Concat([.. minfChildren]));
        var mdia = Box("mdia", Concat(
            PatchDuration(Slice(moov, track.Mdhd), mediaDuration, version1Offset: 24, version0Offset: 16),
            Slice(moov, track.Hdlr),
            minf));
        var trakParts = new List<byte[]> { PatchDuration(Slice(moov, track.Tkhd), movieDuration, version1Offset: 28, version0Offset: 20) };
        if (track.Edts is { } edts)
        {
            trakParts.Add(Slice(moov, edts));
        }

        trakParts.Add(mdia);
        var trak = Box("trak", Concat([.. trakParts]));
        var mvhd = PatchDuration(Slice(moov, track.Mvhd), movieDuration, version1Offset: 24, version0Offset: 16);
        var mvhdBytes = mvhd;
        BinaryPrimitives.WriteUInt32BigEndian(mvhdBytes.AsSpan(mvhdBytes.Length - 4), track.Id + 1);
        return Box("moov", Concat(mvhdBytes, trak));
    }

    private static byte[] SampleTimes(List<Sample> samples)
    {
        var runs = new List<(uint Count, uint Duration)>();
        foreach (var sample in samples)
        {
            if (runs.Count > 0 && runs[^1].Duration == sample.Duration)
            {
                runs[^1] = (runs[^1].Count + 1, sample.Duration);
            }
            else
            {
                runs.Add((1, sample.Duration));
            }
        }

        return FullBox("stts", 0, [Write32((uint)runs.Count), .. runs.Select(r => Concat(Write32(r.Count), Write32(r.Duration)))]);
    }

    private static byte[] CompositionOffsets(List<Sample> samples)
    {
        if (samples.All(s => s.CompositionOffset == 0))
        {
            return [];
        }

        var runs = new List<(uint Count, int Offset)>();
        foreach (var sample in samples)
        {
            if (runs.Count > 0 && runs[^1].Offset == sample.CompositionOffset)
            {
                runs[^1] = (runs[^1].Count + 1, sample.CompositionOffset);
            }
            else
            {
                runs.Add((1, sample.CompositionOffset));
            }
        }

        var version = (byte)(samples.Any(s => s.CompositionOffset < 0) ? 1 : 0);
        return FullBox("ctts", version, [Write32((uint)runs.Count), .. runs.Select(r => Concat(Write32(r.Count), Write32(unchecked((uint)r.Offset))))]);
    }

    private static byte[] SyncSamples(List<Sample> samples)
    {
        if (samples.All(s => s.IsSync))
        {
            return [];
        }

        var sync = new List<byte[]>();
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].IsSync)
            {
                sync.Add(Write32((uint)(i + 1)));
            }
        }

        return FullBox("stss", 0, [Write32((uint)sync.Count), .. sync]);
    }

    private static byte[] SampleSizes(List<Sample> samples) =>
        FullBox("stsz", 0, [Write32(0), Write32((uint)samples.Count), .. samples.Select(s => Write32((uint)s.Size))]);

    private static byte[] ChunkOffsets(List<Sample> samples, long firstOffset)
    {
        var offsets = new long[samples.Count];
        var position = firstOffset;
        for (var i = 0; i < samples.Count; i++)
        {
            offsets[i] = position;
            position += samples[i].Size;
        }

        return position > uint.MaxValue
            ? FullBox("co64", 0, [Write32((uint)offsets.Length), .. offsets.Select(o => Write64((ulong)o))])
            : FullBox("stco", 0, [Write32((uint)offsets.Length), .. offsets.Select(o => Write32((uint)o))]);
    }

    private static byte[] PatchDuration(byte[] box, long duration, int version1Offset, int version0Offset)
    {
        var header = 8;
        if (box[8] == 1)
        {
            BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(header + version1Offset), (ulong)duration);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(header + version0Offset), (uint)Math.Min(duration, uint.MaxValue));
        }

        return box;
    }

    private static byte[] Slice(byte[] data, (int Start, int Length, int Header) box) => data.AsSpan(box.Start, box.Length).ToArray();

    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] FullBox(string type, byte version, params byte[][] parts) =>
        Box(type, Concat([new byte[] { version, 0, 0, 0 }, .. parts]));

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var position = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, position);
            position += part.Length;
        }

        return result;
    }

    private static byte[] Write32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Write64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private readonly record struct BoxInfo(string Type, long Start, long Size, int Header);

    private readonly record struct Sample(long Offset, int Size, uint Duration, int CompositionOffset, bool IsSync);

    /// <summary>Where the video track's boxes are inside the original moov, and its fragment defaults.</summary>
    private sealed class Track
    {
        public uint Id { get; private init; }

        public uint MovieTimescale { get; private init; }

        public uint MediaTimescale { get; private init; }

        public uint DefaultDuration { get; private init; }

        public uint DefaultSize { get; private init; }

        public uint DefaultFlags { get; private init; }

        public (int Start, int Length, int Header) Mvhd { get; private init; }

        public (int Start, int Length, int Header) Tkhd { get; private init; }

        public (int Start, int Length, int Header)? Edts { get; private init; }

        public (int Start, int Length, int Header) Mdhd { get; private init; }

        public (int Start, int Length, int Header) Hdlr { get; private init; }

        public (int Start, int Length, int Header) Minf { get; private init; }

        public (int Start, int Length, int Header) Stsd { get; private init; }

        public static Track Parse(byte[] moov)
        {
            var end = moov.Length;
            var mvhd = Find(moov, 8, end, "mvhd") ?? throw new InvalidDataException("The MP4 has no movie header.");
            var movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mvhd.Start + mvhd.Header + (moov[mvhd.Start + mvhd.Header] == 1 ? 20 : 12)));

            foreach (var trak in Children(moov, 8, end).Where(c => c.Type == "trak"))
            {
                var trakEnd = trak.Start + trak.Length;
                var mdia = Find(moov, trak.Start + trak.Header, trakEnd, "mdia");
                if (mdia is not { } m)
                {
                    continue;
                }

                var mdiaEnd = m.Start + m.Length;
                var hdlr = Find(moov, m.Start + m.Header, mdiaEnd, "hdlr");
                if (hdlr is not { } h || Encoding.ASCII.GetString(moov, h.Start + h.Header + 8, 4) != "vide")
                {
                    continue;
                }

                var tkhd = Find(moov, trak.Start + trak.Header, trakEnd, "tkhd") ?? throw new InvalidDataException("The MP4's video track has no header.");
                var mdhd = Find(moov, m.Start + m.Header, mdiaEnd, "mdhd") ?? throw new InvalidDataException("The MP4's video track has no media header.");
                var minf = Find(moov, m.Start + m.Header, mdiaEnd, "minf") ?? throw new InvalidDataException("The MP4's video track has no media information.");
                var stbl = Find(moov, minf.Start + minf.Header, minf.Start + minf.Length, "stbl") ?? throw new InvalidDataException("The MP4's video track has no sample table.");
                var stsd = Find(moov, stbl.Start + stbl.Header, stbl.Start + stbl.Length, "stsd") ?? throw new InvalidDataException("The MP4's video track has no sample description.");

                var tkhdBody = tkhd.Start + tkhd.Header;
                var id = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(tkhdBody + (moov[tkhdBody] == 1 ? 20 : 12)));
                var mdhdBody = mdhd.Start + mdhd.Header;
                var mediaTimescale = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(mdhdBody + (moov[mdhdBody] == 1 ? 20 : 12)));

                uint duration = 0, size = 0, flags = 0;
                if (Find(moov, 8, end, "mvex") is { } mvex)
                {
                    foreach (var trex in Children(moov, mvex.Start + mvex.Header, mvex.Start + mvex.Length).Where(c => c.Type == "trex"))
                    {
                        var body = trex.Start + trex.Header;
                        if (BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 4)) == id)
                        {
                            duration = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 12));
                            size = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 16));
                            flags = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(body + 20));
                        }
                    }
                }

                return new Track
                {
                    Id = id,
                    MovieTimescale = movieTimescale,
                    MediaTimescale = mediaTimescale,
                    DefaultDuration = duration,
                    DefaultSize = size,
                    DefaultFlags = flags,
                    Mvhd = mvhd,
                    Tkhd = tkhd,
                    Edts = Find(moov, trak.Start + trak.Header, trakEnd, "edts"),
                    Mdhd = mdhd,
                    Hdlr = h,
                    Minf = minf,
                    Stsd = stsd,
                };
            }

            throw new InvalidDataException("The MP4 has no video track.");
        }
    }
}
