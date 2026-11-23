namespace Fungusware.Mft;

/// <summary>One file or folder as listed by <see cref="MftReader.Enumerate"/>.</summary>
/// <param name="Path">Full path, such as <c>C:\Users\Sam\notes.txt</c>.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Length">The length of the file's data (unnamed stream), as Explorer shows it; 0 for a folder.</param>
/// <param name="CreatedUtc">Creation time; <see cref="DateTime.MinValue"/> if the record holds none.</param>
/// <param name="ModifiedUtc">Last write time; <see cref="DateTime.MinValue"/> if the record holds none.</param>
/// <param name="Attributes">The file attributes, such as <see cref="FileAttributes.ReparsePoint"/> for a junction or symbolic link.</param>
public readonly record struct MftEntry(string Path, bool IsFolder, long Length, DateTime CreatedUtc, DateTime ModifiedUtc, FileAttributes Attributes);
