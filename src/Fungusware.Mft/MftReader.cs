using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fungusware.Mft;

/// <summary>
/// Reads the Master File Table of an NTFS volume: one pass over a few hundred megabytes instead of a call per file,
/// so a drive with millions of files is done in seconds. Read only; it opens the volume (<c>\\.\C:</c>) for reading, which needs administrator rights
/// (a Windows service running as LocalSystem has them).
/// </summary>
public static class MftReader
{
    private const int ChunkBytes = 8 << 20;
    private const long RefMask = 0x0000_FFFF_FFFF_FFFF;

    private const uint AttributeList = 0x20, FileName = 0x30, Data = 0x80, IndexAllocation = 0xA0, End = 0xFFFF_FFFF;

    /// <summary>Reads every file and folder on an NTFS volume.</summary>
    /// <param name="volume">A drive such as <c>C:</c>, <c>C:\</c> or any path on it.</param>
    /// <param name="options">What to keep; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the read between pieces of the table.</param>
    /// <exception cref="PlatformNotSupportedException">Not Windows.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not open the volume. Run it as administrator.</exception>
    /// <exception cref="InvalidDataException">The volume is not NTFS, or its MFT is damaged.</exception>
    /// <exception cref="NotSupportedException">The MFT is so fragmented that its own layout is split over extension records.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static MftVolume Read(string volume, MftOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Reading the MFT needs Windows.");
        options ??= new MftOptions();

        string root = Path.GetPathRoot(volume) ?? "";
        if (root.Length < 2 || root[1] != ':' || !char.IsAsciiLetter(root[0])) throw new ArgumentException("Give a drive such as C:\\.", nameof(volume));
        root = char.ToUpperInvariant(root[0]) + @":\";

        using var handle = File.OpenHandle(@"\\.\" + root[..2], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Boot sector: how big a cluster and a file record are, and where the MFT starts
        var boot = new byte[4096];
        ReadExact(handle, boot, 0);
        if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) throw new InvalidDataException(root + " is not NTFS.");

        int sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        int clusterSize = sectorSize * boot[0x0D];
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        sbyte perRecord = (sbyte)boot[0x40];
        int recordSize = perRecord > 0 ? perRecord * clusterSize : 1 << -perRecord;
        if (sectorSize < 512 || clusterSize < sectorSize || recordSize < 1024 || recordSize > 65536 || mftLcn <= 0) throw new InvalidDataException("The boot sector of " + root + " is not valid.");

        // Record 0 is the MFT's own entry; its data attribute says where the rest of the table is
        var first = new byte[Math.Max(recordSize, sectorSize)];
        ReadExact(handle, first, mftLcn * clusterSize);
        if (!Fixups(first.AsSpan(0, recordSize))) throw new InvalidDataException("The first MFT record of " + root + " is not valid.");
        var (runs, mftLength) = MftLayout(first.AsSpan(0, recordSize), clusterSize);

        long recordCount = Math.Min(mftLength / recordSize, int.MaxValue - 1);
        var scan = new Scan((int)recordCount, recordSize, options, root);

        // Read the table in big pieces, the next one while this one is being parsed
        var buffers = new[] { new byte[ChunkBytes + clusterSize], new byte[ChunkBytes + clusterSize] };
        long offset = 0;
        int current = 0;
        int filled = ReadTable(handle, runs, clusterSize, offset, buffers[current], ChunkBytes);

        while (offset < mftLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long nextOffset = offset + ChunkBytes;
            Task<int>? next = null;
            if (nextOffset < mftLength)
            {
                var target = buffers[1 - current];
                next = Task.Run(() => ReadTable(handle, runs, clusterSize, nextOffset, target, ChunkBytes), cancellationToken);
            }

            int usable = (int)Math.Min(filled, mftLength - offset) / recordSize * recordSize;
            var chunk = buffers[current].AsSpan(0, usable);
            for (int at = 0; at + recordSize <= usable; at += recordSize)
                scan.Parse(chunk.Slice(at, recordSize), (int)((offset + at) / recordSize));

            if (next == null) break;
            filled = next.GetAwaiter().GetResult();
            offset = nextOffset;
            current = 1 - current;
        }

        return scan.Finish();
    }

    /// <summary>The runs of the MFT's own data and its length, from record 0.</summary>
    private static (List<Run> Runs, long Length) MftLayout(ReadOnlySpan<byte> record, int clusterSize)
    {
        bool attributeList = false;

        for (int at = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]); at + 8 <= record.Length;)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 4)..]);
            if (type == End || length < 16 || at + length > record.Length) break;

            if (type == AttributeList) attributeList = true;
            if (type == Data && record[at + 8] != 0 && record[at + 9] == 0)
            {
                int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 0x20)..]);
                long size = BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x30)..]);
                return (ParseRuns(record.Slice(at + runOffset, length - runOffset)), size);
            }

            at += length;
        }

        throw attributeList
            ? new NotSupportedException("The MFT's layout is split over extension records.")
            : new InvalidDataException("The MFT has no data attribute.");
    }

    private readonly record struct Run(long Vcn, long Lcn, long Count);

    private static List<Run> ParseRuns(ReadOnlySpan<byte> bytes)
    {
        var runs = new List<Run>();
        long vcn = 0, lcn = 0;

        for (int p = 0; p < bytes.Length && bytes[p] != 0;)
        {
            int lengthBytes = bytes[p] & 0xF, offsetBytes = bytes[p] >> 4;
            p++;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || p + lengthBytes + offsetBytes > bytes.Length) break;

            long count = 0;
            for (int i = lengthBytes - 1; i >= 0; i--) count = (count << 8) | bytes[p + i];
            p += lengthBytes;

            if (offsetBytes == 0) { vcn += count; continue; } // a hole

            long delta = 0;
            for (int i = offsetBytes - 1; i >= 0; i--) delta = (delta << 8) | bytes[p + i];
            if ((bytes[p + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8) delta -= 1L << (8 * offsetBytes);
            p += offsetBytes;

            lcn += delta;
            runs.Add(new Run(vcn, lcn, count));
            vcn += count;
        }

        return runs;
    }

    /// <summary>Reads part of the MFT as one continuous stream, though it lies on the disk in pieces. Offsets and lengths are whole clusters.</summary>
    private static int ReadTable(SafeFileHandle handle, List<Run> runs, int clusterSize, long offset, byte[] buffer, int want)
    {
        int filled = 0;

        foreach (var run in runs)
        {
            long runStart = run.Vcn * clusterSize, runEnd = runStart + run.Count * clusterSize;
            if (runEnd <= offset) continue;
            if (runStart >= offset + want) break;

            long from = Math.Max(runStart, offset), to = Math.Min(runEnd, offset + want);
            ReadExact(handle, buffer.AsSpan((int)(from - offset), (int)(to - from)), run.Lcn * clusterSize + (from - runStart));
            filled = Math.Max(filled, (int)(to - offset));
        }

        return filled;
    }

    private static void ReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        while (!buffer.IsEmpty)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0) throw new EndOfStreamException("The volume ended while the MFT was being read.");
            buffer = buffer[read..];
            offset += read;
        }
    }

    /// <summary>Puts back the bytes NTFS swapped for a check value at the end of every 512 bytes of a record. False if the record is not one, or is torn.</summary>
    private static bool Fixups(Span<byte> record)
    {
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return false;

        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]), count = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (count < 2 || offset + count * 2 > record.Length) return false;

        ushort check = BinaryPrimitives.ReadUInt16LittleEndian(record[offset..]);
        for (int i = 1; i < count; i++)
        {
            int end = i * 512 - 2;
            if (end + 2 > record.Length) break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check) return false;
            record.Slice(offset + i * 2, 2).CopyTo(record[end..]);
        }

        return true;
    }

    /// <summary>What is learned, record by record.</summary>
    private sealed class Scan
    {
        private readonly int _recordSize;
        private readonly MftOptions _options;
        private readonly string _root;
        private readonly int[] _parent;
        private readonly long[] _own;
        private readonly byte[] _kind;
        private readonly byte[] _named;   // 0 no name yet, 1 only a DOS (8.3) name, 2 a real one
        private readonly Dictionary<int, string> _names = [];

        public Scan(int count, int recordSize, MftOptions options, string root)
        {
            _recordSize = recordSize;
            _options = options;
            _root = root;
            _parent = new int[count];
            Array.Fill(_parent, -1);
            _own = new long[count];
            _kind = new byte[count];
            _named = new byte[count];
        }

        public void Parse(Span<byte> record, int index)
        {
            if (record[0] != (byte)'F' || (BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]) & 1) == 0) return; // not a record, or a free one
            if (!Fixups(record)) return;

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
            long baseRef = (long)(BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & (ulong)RefMask);
            bool isBase = baseRef == 0;
            int target = isBase ? index : (int)baseRef;
            if (target >= _kind.Length) return;

            if (isBase) _kind[index] = (flags & 2) != 0 ? MftVolume.Folder : MftVolume.File;

            int used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]), (uint)record.Length);
            bool hasAttributeList = false, sized = false;
            long size = 0, indexBytes = 0;
            int nameAt = -1, nameChars = 0, nameParent = -1;
            byte nameKind = _named[target];

            for (int at = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]); at + 8 <= used;)
            {
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[at..]);
                if (type == End) break;
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 4)..]);
                if (length < 16 || at + length > used) break;

                bool resident = record[at + 8] == 0;
                int nameLength = record[at + 9];

                switch (type)
                {
                    case AttributeList:
                        hasAttributeList = true;
                        break;

                    case FileName when resident:
                    {
                        int value = at + BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 0x14)..]);
                        if (value + 0x42 > used) break;
                        int chars = record[value + 0x40];
                        byte space = record[value + 0x41]; // 0 POSIX, 1 Win32, 2 DOS, 3 both
                        byte rank = space == 2 ? (byte)1 : (byte)2;
                        if (rank > nameKind && value + 0x42 + chars * 2 <= used)
                        {
                            nameKind = rank;
                            nameAt = value + 0x42;
                            nameChars = chars;
                            nameParent = (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(record[value..]) & (ulong)RefMask, (ulong)int.MaxValue);
                        }
                        break;
                    }

                    case Data when nameLength == 0:
                        if (resident)
                        {
                            long length0 = BinaryPrimitives.ReadUInt32LittleEndian(record[(at + 0x10)..]);
                            size = _options.Measure == MftMeasure.Length ? length0 : 0; // lives inside the record: no clusters of its own
                            sized = true;
                        }
                        else if (BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x10)..]) == 0) // the first piece carries the sizes
                        {
                            long allocated = BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x28)..]);
                            long real = BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x30)..]);
                            ushort attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 0x0C)..]);
                            ushort unit = BinaryPrimitives.ReadUInt16LittleEndian(record[(at + 0x22)..]);
                            if ((attrFlags & 0x80FF) != 0 && unit > 0 && at + 0x48 <= used) allocated = BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x40)..]); // compressed or sparse: what is really stored
                            size = _options.Measure == MftMeasure.Length ? real : allocated;
                            sized = true;
                        }
                        break;

                    case IndexAllocation when !resident && _options.Measure == MftMeasure.OnDisk && BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x10)..]) == 0:
                        indexBytes += BinaryPrimitives.ReadInt64LittleEndian(record[(at + 0x28)..]); // the folder's own listing
                        break;
                }

                at += length;
            }

            if (sized) _own[target] = size;
            if (indexBytes > 0) _own[target] += indexBytes;

            if (nameAt >= 0 && nameKind > _named[target])
            {
                _named[target] = nameKind;
                _parent[target] = nameParent < _kind.Length ? nameParent : -1;

                // A name costs more than a size: only folders and big files keep theirs (and fragmented files, whose size may turn up in another record)
                bool keep = !isBase || _kind[target] == MftVolume.Folder || _own[target] >= _options.KeepFileNamesAtLeast || hasAttributeList;
                if (keep) _names[target] = Encoding.Unicode.GetString(record.Slice(nameAt, nameChars * 2));
                else _names.Remove(target);
            }
        }

        public MftVolume Finish()
        {
            var total = new long[_kind.Length];
            long files = 0, folders = 0;

            for (int i = 0; i < _kind.Length; i++)
            {
                byte kind = _kind[i];
                if (kind == 0) continue;
                if (kind == MftVolume.Folder) folders++; else files++;

                long bytes = _own[i];
                if (bytes <= 0) continue;

                // A file counts for every folder above it; a folder's own listing for itself and the folders above
                int at = kind == MftVolume.Folder ? i : _parent[i];
                for (int step = 0; step < 512 && at >= 0 && _kind[at] == MftVolume.Folder; step++)
                {
                    total[at] += bytes;
                    int next = _parent[at];
                    if (next == at) break;
                    at = next;
                }
            }

            return new MftVolume(_root, _parent, _own, total, _kind, _names, files, folders);
        }
    }
}
