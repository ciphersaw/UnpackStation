using System.IO;
using System.Runtime.InteropServices;

namespace UnpackStation.Services.SevenZip;

/// <summary>
/// Dynamic-loading wrapper for 7z.dll: uses only LoadLibrary/GetProcAddress, does not register COM, and does not depend on a system-installed 7-Zip.
/// Only the three functions exported by 7z.dll are called; format information is enumerated by the DLL itself, avoiding hard-coded format CLSIDs.
/// </summary>
internal static class SevenZipNative
{
    // The IInArchive interface id in the 7-Zip SDK
    internal static readonly Guid IidInArchive = new("23170F69-40C1-278A-0000-000600600000");

    private const string RelativeDllPath = @"engines\7z\x64\7z.dll";

    private static readonly object Sync = new();
    private static IntPtr _libraryHandle;
    private static CreateObjectDelegate? _createObject;
    private static GetNumberOfFormatsDelegate? _getNumberOfFormats;
    private static GetHandlerProperty2Delegate? _getHandlerProperty2;

    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, RelativeDllPath);

    internal static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                return;
            }

            if (!File.Exists(LibraryPath))
            {
                throw new FileNotFoundException("The 7-Zip engine 7z.dll was not found; exe installer packages cannot be extracted.", LibraryPath);
            }

            var handle = NativeLibrary.Load(LibraryPath);
            try
            {
                _createObject = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(
                    NativeLibrary.GetExport(handle, "CreateObject"));
                _getNumberOfFormats = Marshal.GetDelegateForFunctionPointer<GetNumberOfFormatsDelegate>(
                    NativeLibrary.GetExport(handle, "GetNumberOfFormats"));
                _getHandlerProperty2 = Marshal.GetDelegateForFunctionPointer<GetHandlerProperty2Delegate>(
                    NativeLibrary.GetExport(handle, "GetHandlerProperty2"));
            }
            catch
            {
                NativeLibrary.Free(handle);
                throw;
            }

            _libraryHandle = handle;
        }
    }

    /// <summary>Enumerates all readable formats built into the DLL and lets 7z detect by file content itself.</summary>
    internal static List<SevenZipFormat> EnumerateFormats()
    {
        EnsureLoaded();
        Marshal.ThrowExceptionForHR(_getNumberOfFormats!(out var count));

        var formats = new List<SevenZipFormat>((int)count);
        for (uint index = 0; index < count; index++)
        {
            var classId = ReadClassId(index);
            if (classId is null)
            {
                // Write-only formats have no IInArchive; skip
                continue;
            }

            formats.Add(new SevenZipFormat(ReadName(index) ?? $"Format#{index}", classId.Value));
        }

        return formats;
    }

    /// <summary>Creates an IInArchive from a format CLSID; returns null on failure.</summary>
    internal static IInArchive? CreateInArchive(Guid classId)
    {
        EnsureLoaded();
        var iid = IidInArchive;
        var hr = _createObject!(ref classId, ref iid, out var unknown);
        if (hr != HResults.SOk || unknown == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            // GetObjectForIUnknown calls AddRef; release the reference handed to us by CreateObject afterwards
            return Marshal.GetObjectForIUnknown(unknown) as IInArchive;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static string? ReadName(uint index)
    {
        var value = new PropVariant();
        if (_getHandlerProperty2!(index, (uint)ArchivePropId.Name, ref value) != HResults.SOk)
        {
            return null;
        }

        // The memory returned by GetHandlerProperty2 may point to static data inside the DLL; deliberately do not call Clear / release it here
        return value.Vt == PropVariant.VtBstr && value.Pointer != IntPtr.Zero
            ? Marshal.PtrToStringUni(value.Pointer)
            : null;
    }

    private static Guid? ReadClassId(uint index)
    {
        var value = new PropVariant();
        if (_getHandlerProperty2!(index, (uint)ArchivePropId.ClassId, ref value) != HResults.SOk)
        {
            return null;
        }

        // As above: do not release the memory returned by the DLL, to avoid wrongly freeing static data
        if (value.Pointer == IntPtr.Zero)
        {
            return null;
        }

        // Older versions return VT_CLSID; 7-Zip 26.x was observed to use VT_BSTR instead, holding the 16 raw GUID bytes
        if (value.Vt == PropVariant.VtClsid)
        {
            return Marshal.PtrToStructure<Guid>(value.Pointer);
        }

        if (value.Vt != PropVariant.VtBstr || Marshal.ReadInt32(value.Pointer, -4) < 16)
        {
            return null;
        }

        var bytes = new byte[16];
        Marshal.Copy(value.Pointer, bytes, 0, 16);
        return new Guid(bytes);
    }

    private enum ArchivePropId : uint
    {
        Name = 0,
        ClassId = 1
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateObjectDelegate(ref Guid classId, ref Guid interfaceId, out IntPtr outObject);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetNumberOfFormatsDelegate(out uint numFormats);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetHandlerProperty2Delegate(uint formatIndex, uint propId, ref PropVariant value);
}

/// <summary>One readable format enumerated from the DLL.</summary>
internal sealed record SevenZipFormat(string Name, Guid ClassId);
