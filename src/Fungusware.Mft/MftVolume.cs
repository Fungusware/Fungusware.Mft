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
    internal const int FirstUserRecord = 16; // records 0 to 15 are the NTFS system files ($MFT, $LogFile, ...), the root (5) aside

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

    internal int RecordCount => _kind.Length;

    internal static DateTime ToDateTime(long fileTime) => fileTime > 0 && fileTime <= 2650467743999999999L ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

    internal static string Combine(string parent, string name) => parent.EndsWith('\\') ? parent + name : parent + '\\' + name;

    /// <summary>The path of a folder, remembered in <paramref name="cache"/> (which the caller keeps for the next ask).</summary>
    internal string FolderPath(int record, Dictionary<int, string> cache)
    {
        var chain = new List<int>(16);
        string path = _root;

        for (int at = record, step = 0; at >= 0 && step < 512; step++)
        {
            if (at == RootRecord) break;
            if (cache.TryGetValue(at, out string? known)) { path = known; break; }
            chain.Add(at);
            int next = _parent[at];
            if (next == at) break;
            at = next;
        }

        for (int k = chain.Count - 1; k >= 0; k--)
            path = cache[chain[k]] = Combine(path, _names.TryGetValue(chain[k], out string? name) ? name : "?");

        return path;
    }

    /// <summary>The record of a folder, found by walking down from the root one name at a time.</summary>
    internal int FindFolder(string folder)
    {
        string? root = Path.GetPathRoot(folder);
        if (root == null || root.Length < 2 || !root.StartsWith(_root[..2], StringComparison.OrdinalIgnoreCase)) throw new DirectoryNotFoundException(folder + " is not on " + _root);

        int at = RootRecord;
        foreach (string part in folder[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            int found = -1;
            for (int i = FirstUserRecord; i < _kind.Length && found < 0; i++)
                if (_kind[i] == Folder && _parent[i] == at && _names.TryGetValue(i, out string? name) && name.Equals(part, StringComparison.OrdinalIgnoreCase)) found = i;
            at = found >= 0 ? found : throw new DirectoryNotFoundException(folder + " was not found on " + _root);
        }

        return at;
    }

    /// <summary>Per record: 1 for a folder that is in the chosen subtree (everything when <paramref name="limit"/> is -1) and not part of the NTFS system files, 2 for one that is not.</summary>
    internal byte[] Classify(int limit)
    {
        var state = new byte[_kind.Length];
        var chain = new List<int>(32);

        for (int i = 0; i < _kind.Length; i++)
        {
            if (_kind[i] != Folder || state[i] != 0) continue;

            chain.Clear();
            byte result = 2;
            for (int at = i, step = 0; at >= 0 && step < 512; step++)
            {
                if (state[at] != 0) { result = state[at]; break; }
                if (_kind[at] != Folder) break;
                if (at < FirstUserRecord && at != RootRecord) break; // a system folder, and so is everything below it

                chain.Add(at);
                if (at == limit || (at == RootRecord && limit < 0)) { result = 1; break; }
                if (at == RootRecord) break;

                int next = _parent[at];
                if (next == at) break;
                at = next;
            }

            foreach (int c in chain) state[c] = result;
        }

        return state;
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
