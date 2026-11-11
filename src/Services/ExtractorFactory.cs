using System.IO;

namespace UnpackStation.Services;

/// <summary>Chooses the extraction implementation by file type: exe uses 7z.dll, msi uses the system msiexec /a.</summary>
public static class ExtractorFactory
{
    private static readonly IPackageExtractor[] Extractors = [new SevenZipExtractor(), new MsiExtractor()];

    public static IPackageExtractor Create(string filePath)
    {
        foreach (var extractor in Extractors)
        {
            if (extractor.CanHandle(filePath))
            {
                return extractor;
            }
        }

        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("This file is not a valid .msi installer package (the file header does not match the Windows Installer format); please make sure the file is intact.");
        }

        throw new NotSupportedException("No extraction engine can handle this file; please make sure it is an .exe or .msi installer package.");
    }
}
