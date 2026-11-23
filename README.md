# Fungusware.Mft

Lists every file and folder on a Windows drive, with sizes, in a couple of seconds. Built for "where did the space go" tools, backup tools and sync tools.

It reads the NTFS Master File Table directly instead of walking the folders, so a system drive with 430,000 files and 195,000 folders takes about 1.7 seconds. It only reads, never writes, has no dependencies and targets .NET 10.

## Find what is using the space

```csharp
using Fungusware.Mft;

MftVolume volume = MftReader.Read("C:");

Console.WriteLine($"{volume.FileCount:N0} files, {volume.TotalBytes / 1e9:0.0} GB");

// Folders up to two levels deep that hold at least 256 MB
foreach (MftItem folder in volume.Folders(minDepth: 1, maxDepth: 2, minBytes: 256 << 20))
    Console.WriteLine($"{folder.Bytes / 1e9,6:0.0} GB  {folder.Path}");

// The ten biggest files over 1 MB
foreach (MftItem file in volume.LargestFiles(count: 10, minBytes: 1 << 20))
    Console.WriteLine($"{file.Bytes / 1e6,6:0} MB  {file.Path}");
```

Example output:

```
432,118 files, 109.4 GB
  38.2 GB  C:\Users
  31.7 GB  C:\Windows
  14.5 GB  C:\Program Files
   9.8 GB  C:\ProgramData
  ...
  4210 MB  C:\hiberfil.sys
  3187 MB  C:\Users\Sam\Videos\holiday.mp4
```

## List every file

For backup and sync tools. Files are streamed as they are found, so a drive with millions of files never has to fit in memory.

```csharp
// Whole drive
foreach (MftEntry e in MftReader.Enumerate("C:"))
    Console.WriteLine($"{e.Length,12:N0}  {e.ModifiedUtc:u}  {e.Path}");

// One folder, files only
foreach (MftEntry e in MftReader.Enumerate("C:", folder: @"C:\Users\Sam", includeFolders: false))
    Console.WriteLine(e.Path);
```

Each entry gives you the path, whether it is a folder, the size, created and modified time (UTC) and the file attributes. A junction or symbolic link shows up as `ReparsePoint`.

## Sizes: on disk or as Explorer shows

```csharp
var options = new MftOptions { Measure = MftMeasure.Length };
MftVolume volume = MftReader.Read("C:", options);
```

- `MftMeasure.OnDisk` (default): the space the file takes on the drive.
- `MftMeasure.Length`: the size Explorer shows.

The total is usually a few percent below what Windows reports as used, because space held by alternate data streams, the change journal and shadow copies does not belong to any folder.

## Try it

`samples/MftScan` is a small command line tool that runs everything above. From an administrator prompt:

```
MftScan C:
MftScan C:  --quick
MftScan C:  --folder=C:\Users
```

## Good to know

- Windows and NTFS only. ReFS, exFAT, FAT and network shares are not supported.
- Needs administrator rights (or a service running as LocalSystem). Without them you get an `UnauthorizedAccessException`.
- Hard links are counted once. Alternate data streams are not counted. The NTFS system files are left out of `Enumerate`.
- If files change while the drive is being read, a new or deleted file may be missed.
- Memory stays low: names are only kept for folders and for files of 1 MiB or more (`MftOptions.KeepFileNamesAtLeast`). Smaller files still count towards their folders.

## Licence

MIT.
