namespace Fungusware.Mft;

/// <summary>A folder or a file with its size.</summary>
/// <param name="Path">Full path, such as <c>C:\Users\Sam\AppData</c>.</param>
/// <param name="Bytes">The size: for a folder, everything inside it.</param>
/// <param name="Depth">Folders below the root: <c>C:\Users</c> is 1. A file has the depth of its folder plus one.</param>
public readonly record struct MftItem(string Path, long Bytes, int Depth);

/// <summary>What <see cref="MftReader"/> found on a volume: sizes rolled up per folder, and the biggest files.</summary>
public sealed class MftVolume
{
    private readonly int[] _parent;      // record number of the parent folder, -1 when unknown
    private readonly long[] _own;        // a file's size; for a folder, the files directly inside it
    private readonly long[] _total;      // a folder's size with everything below it
    private readonly byte[] _kind;       // 0 unused, 1 file, 2 folder
    private readonly Dictionary<int, string> _names;
    private readonly string _root;
    private int[]? _depth;

    internal const byte File = 1, Folder = 2;
    internal const int RootRecord = 5;

    internal MftVolume(string root, int[] parent, long[] own, long[] total, byte[] kind, Dictionary<int, string> names, long files, long folders)
    {
        _root = root;
        _parent = parent;
        _own = own;
        _total = total;
        _kind = kind;
        _names = names;
        FileCount = files;
        FolderCount = folders;
    }

    /// <summary>The volume's root, such as <c>C:\</c>.</summary>
    public string Root => _root;

    /// <summary>Files found (a file with several hard links counts once).</summary>
    public long FileCount { get; }

    /// <summary>Folders found, the root included.</summary>
    public long FolderCount { get; }

    /// <summary>Everything on the volume under the chosen <see cref="MftMeasure"/>.</summary>
    public long TotalBytes => RootRecord < _total.Length ? _total[RootRecord] : 0;

    /// <summary>Folders between two depths whose size is at least <paramref name="minBytes"/>, in no particular order.</summary>
    public IEnumerable<MftItem> Folders(int minDepth, int maxDepth, long minBytes)
    {
        var depth = Depths();
        for (int i = 0; i < _kind.Length; i++)
        {
            if (_kind[i] != Folder || depth[i] < minDepth || depth[i] > maxDepth || _total[i] < minBytes) continue;
            yield return new MftItem(PathOf(i), _total[i], depth[i]);
        }
    }

    /// <summary>The biggest files, largest first. Only files at least <see cref="MftOptions.KeepFileNamesAtLeast"/> long are known by name.</summary>
    public IReadOnlyList<MftItem> LargestFiles(int count, long minBytes)
    {
        var depth = Depths();
        var best = new PriorityQueue<int, long>();

        foreach (int i in _names.Keys)
        {
            if (_kind[i] != File || _own[i] < minBytes || depth[_parent[i] < 0 ? 0 : _parent[i]] < 0) continue;
            best.Enqueue(i, _own[i]);
            if (best.Count > count) best.Dequeue();
        }

        var result = new List<MftItem>(best.Count);
        while (best.TryDequeue(out int i, out _)) result.Add(new MftItem(PathOf(i), _own[i], depth[_parent[i]] + 1));
        result.Reverse();
        return result;
    }

    /// <summary>The path of a record whose name was kept (folders always, files when big enough). A record cut off from the root gets a path that starts with <c>?\</c>.</summary>
    private string PathOf(int record)
    {
        var parts = new List<string>(8);
        int at = record;
        bool rooted = false;

        for (int step = 0; step < 512 && at >= 0; step++)
        {
            if (at == RootRecord) { rooted = true; break; }
            parts.Add(_names.TryGetValue(at, out string? name) ? name : "?");
            int next = _parent[at];
            if (next == at) break;
            at = next;
        }

        parts.Reverse();
        string joined = string.Join('\\', parts);
        return rooted ? _root + joined : @"?\" + joined;
    }

    /// <summary>Folders below the root for every record: 0 for the root, -1 for a record cut off from it (its parent was deleted).</summary>
    private int[] Depths()
    {
        if (_depth != null) return _depth;

        var depth = new int[_kind.Length];
        Array.Fill(depth, -1);
        if (RootRecord < depth.Length) depth[RootRecord] = 0;

        var chain = new List<int>(32);
        for (int i = 0; i < _kind.Length; i++)
        {
            if (_kind[i] != Folder || depth[i] != -1) continue;

            chain.Clear();
            int at = i, known = -1;
            while (at >= 0 && chain.Count < 512)
            {
                if (depth[at] != -1) { known = depth[at]; break; }
                if (_kind[at] != Folder) break;
                chain.Add(at);
                int next = _parent[at];
                if (next == at) break;
                at = next;
            }

            if (known < 0) continue; // never reached the root
            for (int k = chain.Count - 1; k >= 0; k--) depth[chain[k]] = ++known;
        }

        return _depth = depth;
    }
}
