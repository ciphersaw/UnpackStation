using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UnpackStation.Services;

/// <summary>One row of the MSI Directory table: directory id, parent directory id, and default directory name (possibly "short name|long name").</summary>
internal sealed record MsiDirectoryRow(string Name, string Parent, string DefaultDir);

/// <summary>One row of the MSI CustomAction table; actions such as Type 51/307 set the Source property to the path template in Target.</summary>
internal sealed record MsiCustomActionRow(string Action, int Type, string Source, string Target);

/// <summary>
/// The default install location that was read. LeafName is the last directory name of the default install path (Go / Python27),
/// Template is the original form in the package ([ProgramFiles64Folder]Go / [WindowsVolume]Python27), for display only.
/// PackageHasThisLayer means this name already appears in the Directory table, so the layer will naturally exist after extraction and need not be added by the tool.
/// </summary>
internal sealed record MsiInstallInfo(
    string LeafName,
    string Template,
    string Origin,
    bool PackageHasThisLayer);

/// <summary>
/// Opens the MSI database read-only to read the "default install directory name".
/// Uses the system's own msi.dll; does not register COM, install 7-Zip, require administrator privileges, or modify the source file.
/// These paths are absolute system locations (C:\Program Files\Go, C:\Python27) and are never used as the output directory; only the last directory name is taken.
/// </summary>
internal static class MsiInstallDirectoryReader
{
    // Passing NULL for szPersist of MsiOpenDatabaseW means MSIDBOPEN_READONLY (0)
    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr db);

    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);

    [DllImport("msi.dll", EntryPoint = "MsiViewExecute", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", EntryPoint = "MsiViewFetch", ExactSpelling = true)]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetString(IntPtr record, uint field, StringBuilder buffer, ref uint length);

    [DllImport("msi.dll", EntryPoint = "MsiViewClose", ExactSpelling = true)]
    private static extern uint MsiViewClose(IntPtr view);

    [DllImport("msi.dll", EntryPoint = "MsiCloseHandle", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);

    /// <summary>
    /// Reads the number of entries in the File table, used as the denominator for msiexec /a extraction progress.
    /// Returns null when it cannot be read (non-standard MSI / missing table / msi.dll unavailable); the caller falls back to indeterminate progress.
    /// </summary>
    internal static int? TryReadFileCount(string msiPath)
    {
        try
        {
            if (MsiOpenDatabase(msiPath, IntPtr.Zero, out var db) != 0)
            {
                return null;
            }

            try
            {
                var count = Query(db, "SELECT `File` FROM `File`", 1).Count;
                return count > 0 ? count : null;
            }
            finally
            {
                MsiCloseHandle(db);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Any read failure returns null; the caller conservatively falls back to the source file's directory.</summary>
    internal static MsiInstallInfo? TryRead(string msiPath)
    {
        try
        {
            return ReadCore(msiPath);
        }
        catch (Exception)
        {
            // A non-standard MSI, missing table, unavailable msi.dll, or access denied should not affect file selection
            return null;
        }
    }

    private static MsiInstallInfo? ReadCore(string msiPath)
    {
        if (MsiOpenDatabase(msiPath, IntPtr.Zero, out var db) != 0)
        {
            return null;
        }

        try
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Query(db, "SELECT `Property`, `Value` FROM `Property`", 2))
            {
                properties[row[0]] = row[1];
            }

            var directories = Query(db, "SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`", 3)
                .Select(row => new MsiDirectoryRow(row[0], row[1], row[2]))
                .ToList();

            var actions = Query(db, "SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`", 4)
                .Select(row => new MsiCustomActionRow(
                    row[0],
                    int.TryParse(row[1], out var type) ? type : 0,
                    row[2],
                    row[3]))
                .ToList();

            return MsiInstallDirectoryLogic.Resolve(properties, directories, actions);
        }
        finally
        {
            MsiCloseHandle(db);
        }
    }

    private static List<string[]> Query(IntPtr db, string sql, int columns)
    {
        var rows = new List<string[]>();
        if (MsiDatabaseOpenView(db, sql, out var view) != 0)
        {
            return rows;
        }

        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0)
            {
                return rows;
            }

            while (MsiViewFetch(view, out var record) == 0)
            {
                try
                {
                    var values = new string[columns];
                    for (var i = 0; i < columns; i++)
                    {
                        values[i] = ReadField(record, (uint)(i + 1));
                    }

                    rows.Add(values);
                }
                finally
                {
                    MsiCloseHandle(record);
                }
            }
        }
        finally
        {
            MsiViewClose(view);
            MsiCloseHandle(view);
        }

        return rows;
    }

    private static string ReadField(IntPtr record, uint field)
    {
        uint size = 0;
        var probe = MsiRecordGetString(record, field, null!, ref size);
        if (probe != 0 && probe != 234)
        {
            // 234 = ERROR_MORE_DATA is the expected return when probing the length; other errors are treated as an empty value
            return string.Empty;
        }

        // pcchValueBuf input is the total buffer character count (including the trailing null); on ERROR_MORE_DATA the returned length excludes the null
        uint capacity = size + 1;
        var buffer = new StringBuilder((int)capacity);
        return MsiRecordGetString(record, field, buffer, ref capacity) == 0
            ? buffer.ToString()
            : string.Empty;
    }
}

/// <summary>
/// Infers the default install directory name from the read tables. Kept as pure logic so it can be unit tested without a real MSI.
/// Priority: the Directory row pointed to by WIXUI_INSTALLDIR → a CustomAction that sets a directory property → a directory property in the Property table.
/// </summary>
internal static class MsiInstallDirectoryLogic
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
        "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    internal static MsiInstallInfo? Resolve(
        IReadOnlyDictionary<string, string> properties,
        IReadOnlyList<MsiDirectoryRow> directories,
        IReadOnlyList<MsiCustomActionRow> actions)
    {
        // WiX family (Go / Node / PuTTY): WIXUI_INSTALLDIR points to INSTALLDIR, whose DefaultDir is the default install directory name
        if (properties.TryGetValue("WIXUI_INSTALLDIR", out var installDirKey) &&
            FindDirectory(directories, installDirKey) is { } target)
        {
            var leaf = CleanDefaultDir(target.DefaultDir);
            if (IsSafeLeafName(leaf))
            {
                return new MsiInstallInfo(
                    leaf,
                    $"[{target.Parent}]{leaf}",
                    "WIXUI_INSTALLDIR + Directory table",
                    HasDirectoryNamed(directories, leaf));
            }
        }

        // Old-style MSI (Python 2.7): a CustomAction sets TARGETDIR to [WindowsVolume]Python27
        foreach (var action in actions)
        {
            if (FindDirectory(directories, action.Source) is null)
            {
                continue;
            }

            var leaf = ParseTemplateLeaf(action.Target);
            if (!IsSafeLeafName(leaf))
            {
                continue;
            }

            return new MsiInstallInfo(
                leaf,
                action.Target,
                $"CustomAction {action.Action}",
                HasDirectoryNamed(directories, leaf));
        }

        // Final fallback: the Property table writes the directory property directly
        foreach (var key in new[] { "TARGETDIR", "INSTALLDIR", "INSTALLFOLDER", "APPLICATIONFOLDER" })
        {
            if (!properties.TryGetValue(key, out var value))
            {
                continue;
            }

            var leaf = ParseTemplateLeaf(value);
            if (!IsSafeLeafName(leaf))
            {
                continue;
            }

            return new MsiInstallInfo(leaf, value, $"Property {key}", HasDirectoryNamed(directories, leaf));
        }

        return null;
    }

    /// <summary>DefaultDir is commonly a "8.3 short name|long name" two-part format, or just a placeholder . / SourceDir.</summary>
    internal static string CleanDefaultDir(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var name = value.Split('|')[^1].Trim();
        return name is "." or ".." or "SourceDir" ? string.Empty : name;
    }

    /// <summary>
    /// Extracts the directory name from templates like [WindowsVolume]Python27.
    /// Only accepts the case where exactly one directory segment remains after removing placeholders; multiple segments (e.g. [TARGETDIR]Lib\compileall.py) are too uncertain and are given up.
    /// </summary>
    internal static string ParseTemplateLeaf(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var rest = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '[')
            {
                var end = template.IndexOf(']', i + 1);
                if (end < 0)
                {
                    return string.Empty;
                }

                i = end;
                continue;
            }

            rest.Append(template[i]);
        }

        var leaf = rest.ToString().Trim();
        return leaf.Contains('\\') || leaf.Contains('/') ? string.Empty : leaf.TrimEnd('\\', '/');
    }

    /// <summary>Whether a directory name can be safely appended to the output path. Never bring absolute system paths or MSI variables into the output directory.</summary>
    internal static bool IsSafeLeafName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return false;
        }

        if (name is "." or ".." || ReservedNames.Contains(name))
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // Unexpanded MSI variables / environment variables are not used as directory names
        return !name.Contains('[') && !name.Contains(']') && !name.Contains('%') && !name.Contains('$');
    }

    private static MsiDirectoryRow? FindDirectory(IReadOnlyList<MsiDirectoryRow> directories, string key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : directories.FirstOrDefault(row => string.Equals(row.Name, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>If this name appears in the Directory table, msiexec /a naturally produces that layer after extraction and the tool need not add it.</summary>
    private static bool HasDirectoryNamed(IReadOnlyList<MsiDirectoryRow> directories, string leaf) =>
        directories.Any(row => string.Equals(CleanDefaultDir(row.DefaultDir), leaf, StringComparison.OrdinalIgnoreCase));
}
