// Package the published UnpackStation output into a distributable zip.
//
// Usage (from the repository root):
//   dotnet run PackRelease.cs
// Optional paths:
//   dotnet run PackRelease.cs -- <publishDir> <outputZip>
//
// Uses only the .NET SDK that builds this project: no 7-Zip, no PowerShell
// script execution policy, no third-party tool and no machine-specific path.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

string publishDir = args.Length > 0 ? args[0] : "publish";
string output = args.Length > 1
    ? args[1]
    : Path.Combine("dist", "UnpackStation-win-x64.zip");

// These entries must sit at the archive root, with no "publish/" prefix.
string[] required =
{
    "UnpackStation.exe",
    "engines/7z/License.txt",
    "engines/7z/x64/7z.dll"
};

if (!Directory.Exists(publishDir))
{
    Console.Error.WriteLine("Publish directory not found: " + Path.GetFullPath(publishDir));
    Console.Error.WriteLine("Run 'dotnet publish src/UnpackStation.csproj -c Release -r win-x64 --self-contained true -o publish' first.");
    return 1;
}

foreach (string item in required)
{
    string path = Path.Combine(publishDir, item.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(path))
    {
        Console.Error.WriteLine("Incomplete publish output, missing: " + item);
        return 1;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
if (File.Exists(output))
{
    File.Delete(output);
}

// Write entries explicitly: ZipFile.CreateFromDirectory omits directory
// entries, and relative names keep UnpackStation.exe at the archive root.
using (FileStream stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (string dir in Directory.GetDirectories(publishDir, "*", SearchOption.AllDirectories))
    {
        string entryName = Path.GetRelativePath(publishDir, dir).Replace('\\', '/') + "/";
        archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
    }
    foreach (string file in Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories))
    {
        string entryName = Path.GetRelativePath(publishDir, file).Replace('\\', '/');
        archive.CreateEntryFromFile(file, entryName, CompressionLevel.SmallestSize);
    }
}

using (ZipArchive archive = ZipFile.OpenRead(output))
{
    HashSet<string> names = new HashSet<string>(
        archive.Entries.Select(e => e.FullName.Replace('\\', '/')),
        StringComparer.Ordinal);

    foreach (string item in required)
    {
        if (!names.Contains(item))
        {
            Console.Error.WriteLine("Archive is missing root entry: " + item);
            return 1;
        }
    }

    if (names.Any(n => n.StartsWith("publish/", StringComparison.Ordinal)))
    {
        Console.Error.WriteLine("Archive contains an unexpected publish/ prefix.");
        return 1;
    }

    int files = archive.Entries.Count(e => !e.FullName.EndsWith("/", StringComparison.Ordinal));
    int dirs = archive.Entries.Count(e => e.FullName.EndsWith("/", StringComparison.Ordinal));
    Console.WriteLine("Created " + Path.GetFullPath(output));
    Console.WriteLine(string.Format(
        "  {0} files, {1} folders, {2:N0} bytes",
        files, dirs, new FileInfo(output).Length));
}

return 0;
