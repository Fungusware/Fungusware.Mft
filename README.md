# Fungusware.Mft

Reads an NTFS volume's Master File Table directly. Every file and folder name and size on a drive in a couple of seconds, for "where did the space go" tools. Read only, managed code, no dependencies, .NET 10.

Walking a drive with `Directory.EnumerateFileSystemInfos` makes one call per file. The MFT holds the same information in one contiguous table, so reading it in 8 MB pieces is limited by the disk, not by the file system API. On a system drive with 430,000 files and 195,000 folders the read takes about 1.7 s.

```csharp
using Fungusware.Mft;

MftVolume volume = MftReader.Read("C:");                          // needs administrator rights

Console.WriteLine($"{volume.FileCount:N0} files, {volume.TotalBytes / 1e9:0.0} GB");

foreach (MftItem folder in volume.Folders(minDepth: 1, maxDepth: 2, minBytes: 256 << 20))
    Console.WriteLine($"{folder.Bytes / 1e9,6:0.0} GB  {folder.Path}");

foreach (MftItem file in volume.LargestFiles(count: 10, minBytes: 1 << 20))
    Console.WriteLine($"{file.Bytes / 1e6,6:0} MB  {file.Path}");
```

`samples/MftScan` is this as a command line tool: `MftScan C:` from an elevated prompt.

## What it measures

- `MftMeasure.OnDisk` (default): space the file takes on the volume, from its allocated clusters (the stored size for compressed and sparse files). Files small enough to live inside their MFT record count as 0. A folder's own index counts towards that folder.
- `MftMeasure.Length`: the length of the file's data, as Explorer shows it.

Only the unnamed data stream is counted; alternate data streams are not. A file with several hard links is counted once. System files (`$MFT`, `$LogFile`, `$Secure`, ...) are included, so the folder sizes add up to what the volume reports as used, within a few percent.

## Memory

A volume with millions of files is read into arrays of about 17 bytes per MFT record. Names are the expensive part, so only folders and files of at least `MftOptions.KeepFileNamesAtLeast` (1 MiB) keep theirs. Everything else still counts towards its folders.

## Requirements and limits

- Windows, NTFS. Opening `\.\C:` needs administrator rights (`UnauthorizedAccessException` otherwise); a service running as LocalSystem has them. Nothing is written.
- ReFS, exFAT, FAT and network shares are not read. Use a normal directory walk for those.
- An MFT so fragmented that its own layout needs an attribute list throws `NotSupportedException`.
- The table is read while the volume is in use, so a file created or deleted during the read may be missed or counted at its old size.

## Licence

MIT.
