using System;
using System.IO;

namespace UnpackStation.Services;

/// <summary>
/// Computes the default output directory: it is simply the source file's directory, without an extra "file name_extracted" layer.
/// The top-level directory shipped with the installer package (e.g. Go\) is the natural isolation layer in this scenario;
/// only when the source file happens to be at a drive root does it fall back to a separate subdirectory, to avoid scattering files at the root.
/// It only chooses a name and does not create the directory; actual creation is done by the later extraction phase.
/// </summary>
public static class OutputDirectoryResolver
{
    public static string Resolve(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullPath = Path.GetFullPath(sourcePath);
        var parentDir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The source file path has no parent directory, so the output location cannot be determined.", nameof(sourcePath));

        // parentDir still has a parent => not a drive root / UNC root, so we can extract directly into it
        if (Path.GetDirectoryName(parentDir) is not null)
        {
            return parentDir;
        }

        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            // Edge case (e.g. a file name that is only an extension): give a readable fallback name
            baseName = "package";
        }

        return Path.Combine(parentDir, baseName + "_extracted");
    }
}
