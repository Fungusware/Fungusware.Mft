using System.Diagnostics;
using Fungusware.Mft;

// mftscan C:          the biggest folders and files on a drive (run as administrator)
// mftscan C: --length sizes as Explorer shows them, not space on disk
// mftscan C: --list   stream every file with length, times and attributes (counts them, prints the first ten)
// mftscan C: --list=C:\Users   the same for one folder and everything below it
string drive = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "C:";
string? list = args.FirstOrDefault(a => a == "--list" || a.StartsWith("--list="));
var options = new MftOptions { Measure = args.Contains("--length") ? MftMeasure.Length : MftMeasure.OnDisk };

var clock = Stopwatch.StartNew();

if (list != null)
{
    string? under = list.Contains('=') ? list[(list.IndexOf('=') + 1)..] : null;
    long files = 0, folders = 0, bytes = 0, shown = 0;
    try
    {
        foreach (var e in MftReader.Enumerate(drive, under))
        {
            if (e.IsFolder) folders++; else { files++; bytes += e.Length; }
            if (shown++ < 10) Console.WriteLine($"  {(e.IsFolder ? "<DIR>" : Size(e.Length)),10}  {e.ModifiedUtc:yyyy-MM-dd HH:mm:ss}  {e.Attributes,-30}  {e.Path}");
        }
    }
    catch (UnauthorizedAccessException) { Console.Error.WriteLine("Reading the volume needs administrator rights. Run this from an elevated prompt."); return 1; }
    catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) { Console.Error.WriteLine(e.Message); return 1; }

    Console.WriteLine($"{files:N0} files, {folders:N0} folders, {Size(bytes)} of data  (listed in {clock.Elapsed.TotalSeconds:0.0} s, peak memory {Process.GetCurrentProcess().PeakWorkingSet64 / (1 << 20):N0} MB)");
    return 0;
}

MftVolume volume;
try { volume = MftReader.Read(drive, options); }
catch (UnauthorizedAccessException) { Console.Error.WriteLine("Reading the volume needs administrator rights. Run this from an elevated prompt."); return 1; }
catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) { Console.Error.WriteLine(e.Message); return 1; }
var read = clock.Elapsed;

Console.WriteLine($"{volume.Root}  {volume.FileCount:N0} files, {volume.FolderCount:N0} folders, {Size(volume.TotalBytes)}  (read in {read.TotalSeconds:0.0} s)");
Console.WriteLine();
Console.WriteLine("Biggest folders");
foreach (var f in volume.Folders(1, 2, 1L << 28).OrderByDescending(f => f.Bytes).Take(15))
    Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

Console.WriteLine();
Console.WriteLine("Biggest files");
foreach (var f in volume.LargestFiles(10, 1L << 20))
    Console.WriteLine($"  {Size(f.Bytes),10}  {f.Path}");

Console.WriteLine();
Console.WriteLine($"Total {clock.Elapsed.TotalSeconds:0.0} s");
return 0;

static string Size(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
    >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
    _ => $"{bytes / 1024.0:0} KB",
};
