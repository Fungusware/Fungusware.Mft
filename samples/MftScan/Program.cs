using System.Diagnostics;
using Fungusware.Mft;

// MftScan C:                 shows every feature of Fungusware.Mft on a drive (run as administrator)
// MftScan C: --folder=C:\Users   the folder-scoped parts use this folder (default: the biggest top-level folder)
// MftScan C: --quick         only the Read part (folders and files by size), skips the streaming parts
// Ctrl+C stops a read cleanly.
string drive = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "C:";
string? folderArg = args.FirstOrDefault(a => a.StartsWith("--folder="))?["--folder=".Length..];
bool quick = args.Contains("--quick");

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var token = cancel.Token;
var total = Stopwatch.StartNew();

try
{
    // 1. Read: the whole volume as folder totals and the largest files, sized as space on disk
    Section("1. MftReader.Read - space on disk (MftMeasure.OnDisk)");
    var clock = Stopwatch.StartNew();
    var onDisk = MftReader.Read(drive, new MftOptions { Measure = MftMeasure.OnDisk }, token);
    Console.WriteLine($"{onDisk.Root}  {onDisk.FileCount:N0} files, {onDisk.FolderCount:N0} folders, {Size(onDisk.TotalBytes)}  (read in {clock.Elapsed.TotalSeconds:0.0} s)");

    Console.WriteLine();
    Console.WriteLine("Biggest top-level folders (Folders(1, 1, 0))");
    var top = onDisk.Folders(1, 1, 0).OrderByDescending(f => f.Bytes).ToList();
    foreach (var f in top.Take(8)) Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

    Console.WriteLine();
    Console.WriteLine("Biggest folders two levels down, 256 MB or more (Folders(2, 2, 256 MB))");
    foreach (var f in onDisk.Folders(2, 2, 1L << 28).OrderByDescending(f => f.Bytes).Take(10)) Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

    Console.WriteLine();
    Console.WriteLine("Biggest files, 1 MB or more (LargestFiles(10, 1 MB))");
    foreach (var f in onDisk.LargestFiles(10, 1L << 20)) Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

    // 2. Read with the other measure and a lower name threshold
    Section("2. MftOptions - Length instead of space on disk, names kept from 64 KB");
    clock.Restart();
    var length = MftReader.Read(drive, new MftOptions { Measure = MftMeasure.Length, KeepFileNamesAtLeast = 64 << 10 }, token);
    Console.WriteLine($"Length (as Explorer shows):  {Size(length.TotalBytes)}");
    Console.WriteLine($"On disk:                     {Size(onDisk.TotalBytes)}");
    Console.WriteLine($"(second read {clock.Elapsed.TotalSeconds:0.0} s)");
    Console.WriteLine();
    Console.WriteLine("Biggest files, 64 KB or more (LargestFiles(5, 64 KB)) - possible only because of KeepFileNamesAtLeast");
    foreach (var f in length.LargestFiles(5, 64 << 10)) Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

    if (quick) { Console.WriteLine(); Console.WriteLine($"Total {total.Elapsed.TotalSeconds:0.0} s"); return 0; }

    // 3. Enumerate: the whole volume, streamed, one pass of statistics
    Section("3. MftReader.Enumerate - every file and folder, streamed");
    clock.Restart();
    long files = 0, folders = 0, bytes = 0;
    var kinds = new Dictionary<FileAttributes, long>();
    FileAttributes[] interesting = [FileAttributes.Hidden, FileAttributes.System, FileAttributes.ReadOnly, FileAttributes.Compressed, FileAttributes.Encrypted, FileAttributes.SparseFile, FileAttributes.ReparsePoint, FileAttributes.Offline];
    var newest = new List<MftEntry>();
    MftEntry oldest = default;
    bool haveOldest = false;
    foreach (var e in MftReader.Enumerate(drive, cancellationToken: token))
    {
        if (e.IsFolder) { folders++; }
        else
        {
            files++; bytes += e.Length;
            if (!haveOldest || e.CreatedUtc < oldest.CreatedUtc) { oldest = e; haveOldest = true; }
            Keep(newest, e, 5);
        }
        foreach (var a in interesting) if ((e.Attributes & a) != 0) kinds[a] = kinds.GetValueOrDefault(a) + 1;
    }
    Console.WriteLine($"{files:N0} files, {folders:N0} folders, {Size(bytes)} of data  (streamed in {clock.Elapsed.TotalSeconds:0.0} s, peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20):N0} MB)");
    Console.WriteLine();
    Console.WriteLine("Entries with each attribute (MftEntry.Attributes)");
    foreach (var a in interesting) Console.WriteLine($"  {a,-14} {kinds.GetValueOrDefault(a),12:N0}");
    Console.WriteLine();
    Console.WriteLine("Most recently modified files (MftEntry.ModifiedUtc)");
    foreach (var e in newest) Console.WriteLine($"  {e.ModifiedUtc:yyyy-MM-dd HH:mm:ss}  {Size(e.Length),10}  {e.Path}");
    if (haveOldest)
    {
        Console.WriteLine();
        Console.WriteLine("Oldest file by creation time (MftEntry.CreatedUtc)");
        Console.WriteLine($"  {oldest.CreatedUtc:yyyy-MM-dd HH:mm:ss}  {Size(oldest.Length),10}  {oldest.Path}");
    }

    // 4. Enumerate one folder, files only
    string? under = folderArg ?? top.FirstOrDefault().Path;
    if (under == null) return 0;
    Section($"4. Enumerate(folder: \"{under}\", includeFolders: false) - files only, one folder");
    clock.Restart();
    long n = 0, b = 0, shown = 0;
    foreach (var e in MftReader.Enumerate(drive, under, includeFolders: false, cancellationToken: token))
    {
        n++; b += e.Length;
        if (shown++ < 10) Console.WriteLine($"  {Size(e.Length),10}  {e.ModifiedUtc:yyyy-MM-dd HH:mm:ss}  {e.Attributes,-24}  {e.Path}");
    }
    Console.WriteLine($"... {n:N0} files, {Size(b)}  ({clock.Elapsed.TotalSeconds:0.0} s)");

    // 5. A folder that does not exist
    Section("5. Errors - DirectoryNotFoundException for a missing folder");
    try { foreach (var _ in MftReader.Enumerate(drive, Path.Combine(drive + "\\", "no such folder " + Guid.NewGuid().ToString("N")), cancellationToken: token)) { } }
    catch (DirectoryNotFoundException e) { Console.WriteLine($"  caught: {e.Message}"); }
}
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Reading the volume needs administrator rights. Run this from an elevated prompt."); return 1; }
catch (OperationCanceledException) { Console.Error.WriteLine("Stopped."); return 2; }
catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) { Console.Error.WriteLine(e.Message); return 1; }

Console.WriteLine();
Console.WriteLine($"Total {total.Elapsed.TotalSeconds:0.0} s");
return 0;

static void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 78));
    Console.WriteLine(title);
    Console.WriteLine(new string('=', 78));
}

// Keeps the `count` most recently modified entries seen so far
static void Keep(List<MftEntry> list, MftEntry e, int count)
{
    if (list.Count == count && e.ModifiedUtc <= list[^1].ModifiedUtc) return;
    int at = list.FindIndex(x => x.ModifiedUtc < e.ModifiedUtc);
    list.Insert(at < 0 ? list.Count : at, e);
    if (list.Count > count) list.RemoveAt(count);
}

static string Size(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
    _ => $"{bytes / 1024.0:0} KB",
};
