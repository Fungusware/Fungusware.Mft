using System.Diagnostics;
using Fungusware.Mft;

// mftscan C:          the biggest folders and files on a drive (run as administrator)
// mftscan C: --length sizes as Explorer shows them, not space on disk
string drive = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "C:";
var options = new MftOptions { Measure = args.Contains("--length") ? MftMeasure.Length : MftMeasure.OnDisk };

var clock = Stopwatch.StartNew();
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
