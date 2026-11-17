using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using UnpackStation.Models;

namespace UnpackStation.Services.SevenZip;

internal static class HResults
{
    internal const int SOk = 0;
    internal const int SFalse = 1;
    internal const int EAbort = unchecked((int)0x80004004);
    internal const int EFail = unchecked((int)0x80004005);
}

/// <summary>
/// The native 24-byte PROPVARIANT layout (x64): vt + 3 reserved words + an 8-byte-aligned union.
/// Only the types actually used are read, to avoid introducing extra abstractions.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    internal const ushort VtEmpty = 0;
    internal const ushort VtBool = 11;
    internal const ushort VtBstr = 8;
    internal const ushort VtUi4 = 19;
    internal const ushort VtI4 = 3;
    internal const ushort VtUi8 = 21;
    internal const ushort VtI8 = 20;
    internal const ushort VtUi2 = 18;
    internal const ushort VtUi1 = 17;
    internal const ushort VtClsid = 72;
    internal const ushort VtFileTime = 64;

    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public ulong UInt64Value;
    [FieldOffset(8)] public long Int64Value;
    [FieldOffset(8)] public uint UInt32Value;
    [FieldOffset(8)] public int Int32Value;
    [FieldOffset(8)] public short BoolValue;
    [FieldOffset(8)] public ushort UInt16Value;
    [FieldOffset(8)] public byte ByteValue;
    [FieldOffset(8)] public FILETIME FileTimeValue;

    public bool AsBool() => Vt == VtBool && BoolValue != 0;

    public ulong AsUInt64() => Vt switch
    {
        VtUi8 => UInt64Value,
        VtI8 when Int64Value > 0 => (ulong)Int64Value,
        VtUi4 => UInt32Value,
        VtI4 when Int32Value > 0 => (ulong)Int32Value,
        VtUi2 => UInt16Value,
        VtUi1 => ByteValue,
        _ => 0
    };

    /// <summary>Only for GetProperty return values (memory ownership belongs to the caller); do not call it on GetHandlerProperty2 return values.</summary>
    public void Clear()
    {
        switch (Vt)
        {
            case VtEmpty:
            case VtBool:
            case VtUi1:
            case VtUi2:
            case VtUi4:
            case VtUi8:
            case VtI4:
            case VtI8:
            case VtFileTime:
                Vt = VtEmpty;
                break;
            default:
                PropVariantClear(ref this);
                Vt = VtEmpty;
                break;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

internal enum ItemPropId : uint
{
    Path = 3,
    IsFolder = 6,
    Size = 7,
    Encrypted = 15,
    IsAnti = 21
}

internal enum AskMode : int
{
    Extract = 0,
    Test = 1,
    Skip = 2
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600600000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig]
    int Open(
        [In, MarshalAs(UnmanagedType.Interface)] IInStream stream,
        [In] ref ulong maxCheckStartPosition,
        [In, MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback openCallback);

    void Close();
    uint GetNumberOfItems();
    void GetProperty(uint index, ItemPropId propId, ref PropVariant value);

    [PreserveSig]
    int Extract(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[] indices,
        uint numItems,
        int testMode,
        [In, MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback extractCallback);

    void GetArchiveProperty(uint propId, ref PropVariant value);
    uint GetNumberOfProperties();
    void GetPropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propId, out ushort varType);
    uint GetNumberOfArchiveProperties();
    void GetArchivePropertyInfo(uint index, [MarshalAs(UnmanagedType.BStr)] out string name, out uint propId, out ushort varType);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300010000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialInStream
{
    uint Read([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300030000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    // The IInStream vtable is IUnknown + Read + Seek; declared flat here, in the same order as the 7-Zip SDK
    uint Read([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size);
    void Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000300020000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig]
    int Write([In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, uint size, IntPtr processedSize);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    void SetTotal(IntPtr files, IntPtr bytes);
    void SetCompleted(IntPtr files, IntPtr bytes);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000500100000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
}

[ComImport]
[Guid("23170F69-40C1-278A-0000-000600200000")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    // Inherits IProgress; vtable order: SetTotal / SetCompleted / GetStream / PrepareOperation / SetOperationResult
    [PreserveSig] int SetTotal(ulong total);
    [PreserveSig] int SetCompleted(IntPtr completeValue);

    [PreserveSig]
    int GetStream(
        uint index,
        [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream outStream,
        int askExtractMode);

    [PreserveSig] int PrepareOperation(int askExtractMode);
    [PreserveSig] int SetOperationResult(int operationResult);
}

/// <summary>Wraps the source FileStream into the IInStream that 7-Zip expects.</summary>
internal sealed class InStreamWrapper : IInStream, ISequentialInStream, IDisposable
{
    private readonly Stream _stream;

    internal InStreamWrapper(Stream stream) => _stream = stream;

    public uint Read(byte[] data, uint size) => (uint)_stream.Read(data, 0, (int)size);

    public void Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        var position = _stream.Seek(offset, (SeekOrigin)seekOrigin);
        if (newPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(newPosition, position);
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Callback when opening an archive; also captures the "password required" request to distinguish encrypted packages from unrecognized formats.</summary>
internal sealed class OpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword
{
    internal bool PasswordRequested { get; private set; }

    public void SetTotal(IntPtr files, IntPtr bytes)
    {
    }

    public void SetCompleted(IntPtr files, IntPtr bytes)
    {
    }

    public int CryptoGetTextPassword(out string password)
    {
        // The current version does not support password input; set a flag, let Open fail, and let the layer above translate it into a human-readable message
        PasswordRequested = true;
        password = string.Empty;
        return HResults.EFail;
    }
}

/// <summary>Skipped entries still have to be extracted by 7-Zip to continue a solid block; this stream discards the bytes.</summary>
internal sealed class DiscardOutStream : ISequentialOutStream
{
    private readonly ExtractionBudget _budget;

    internal DiscardOutStream(ExtractionBudget budget) => _budget = budget;

    public int Write(byte[] data, uint size, IntPtr processedSize)
    {
        // Discarded bytes are also really decompressed, so they count toward the bomb budget too
        if (!_budget.AddBytes(size))
        {
            return HResults.EAbort;
        }

        if (processedSize != IntPtr.Zero)
        {
            Marshal.WriteInt32(processedSize, (int)size);
        }

        return HResults.SOk;
    }
}

/// <summary>Writes extracted bytes to disk; returns E_ABORT on cancellation or when the budget is exceeded so 7-Zip stops immediately.</summary>
internal sealed class OutStreamWrapper : ISequentialOutStream, IDisposable
{
    private readonly Stream _stream;
    private readonly CancellationToken _ct;
    private readonly ExtractionBudget _budget;

    internal OutStreamWrapper(Stream stream, CancellationToken ct, ExtractionBudget budget)
    {
        _stream = stream;
        _ct = ct;
        _budget = budget;
    }

    public int Write(byte[] data, uint size, IntPtr processedSize)
    {
        if (_ct.IsCancellationRequested || !_budget.AddBytes(size))
        {
            return HResults.EAbort;
        }

        _stream.Write(data, 0, (int)size);
        if (processedSize != IntPtr.Zero)
        {
            Marshal.WriteInt32(processedSize, (int)size);
        }

        return HResults.SOk;
    }

    public void Dispose()
    {
        _stream.Flush();
        _stream.Dispose();
    }
}

/// <summary>Per-file callback: creates streams for the pre-validated target paths and reports progress.</summary>
internal sealed class ExtractCallback : IArchiveExtractCallback, IDisposable
{
    private readonly IReadOnlyDictionary<uint, string> _targets;
    private readonly IProgress<ExtractionProgress> _progress;
    private readonly CancellationToken _ct;
    private readonly ulong _totalBytes;
    private readonly ExtractionBudget _budget;
    private readonly List<OutStreamWrapper> _openStreams = new();

    internal int WrittenFileCount { get; private set; }
    internal List<string> Errors { get; } = new();
    internal List<string> WrittenFiles { get; } = new();
    internal bool WrongPassword { get; private set; }
    internal ExtractionBudget Budget => _budget;

    internal ExtractCallback(
        IReadOnlyDictionary<uint, string> targets,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct,
        ulong totalBytes,
        ExtractionBudget budget)
    {
        _targets = targets;
        _progress = progress;
        _ct = ct;
        _totalBytes = totalBytes;
        _budget = budget;
    }

    public int SetTotal(ulong total) => HResults.SOk;

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1024UL * 1024UL * 1024UL => $"{bytes / (1024d * 1024d * 1024d):0.00} GB",
        >= 1024UL * 1024UL => $"{bytes / (1024d * 1024d):0.0} MB",
        >= 1024UL => $"{bytes / 1024d:0.0} KB",
        _ => $"{bytes} B"
    };

    public int SetCompleted(IntPtr completeValue)
    {
        if (_ct.IsCancellationRequested || _budget.Exceeded)
        {
            return HResults.EAbort;
        }

        if (completeValue != IntPtr.Zero && _totalBytes > 0)
        {
            var done = unchecked((ulong)Marshal.ReadInt64(completeValue));
            var percent = (int)Math.Min(99, done * 100 / _totalBytes);
            var detail = $"{FormatBytes(Math.Min(done, _totalBytes))} / {FormatBytes(_totalBytes)}";
            _progress.Report(new ExtractionProgress(percent, ExtractionStage.Extracting, detail));
        }
        else
        {
            // When the total size is unknown, do not fabricate a percentage; let the UI use Marquee
            _progress.Report(new ExtractionProgress(-1, ExtractionStage.Extracting, "Byte size unknown"));
        }

        return HResults.SOk;
    }

    public int GetStream(uint index, out ISequentialOutStream outStream, int askExtractMode)
    {
        if (_ct.IsCancellationRequested || _budget.Exceeded)
        {
            outStream = null!;
            return HResults.EAbort;
        }

        if (askExtractMode != (int)AskMode.Extract || !_targets.TryGetValue(index, out var target))
        {
            // Do not return S_FALSE: 7-Zip 26.x treats it as "abort the whole Extract",
            // in a solid archive, any skipped entry would make all subsequent files fail to be written.
            // Give a stream that only discards bytes so 7-Zip finishes decompressing normally and continues to the next entry.
            outStream = new DiscardOutStream(_budget);
            return HResults.SOk;
        }

        outStream = null!;

        if (!_budget.AddFile())
        {
            return HResults.EAbort;
        }

        try
        {
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            // CreateNew is the last safeguard: even if another process creates the file after the planning phase, the user's file is not overwritten
            var fileStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            var wrapper = new OutStreamWrapper(fileStream, _ct, _budget);
            _openStreams.Add(wrapper);
            outStream = wrapper;
            WrittenFileCount++;
            WrittenFiles.Add(target);
            return HResults.SOk;
        }
        catch (Exception ex)
        {
            Errors.Add($"Cannot write entry #{index} ({target}): {ex.Message}");
            return HResults.EFail;
        }
    }

    public int PrepareOperation(int askExtractMode) => HResults.SOk;

    public int SetOperationResult(int operationResult)
    {
        if (operationResult == 9)
        {
            WrongPassword = true;
        }
        else if (operationResult != 0)
        {
            Errors.Add($"7-Zip reported that extracting the entry failed, result code {operationResult}");
        }

        return HResults.SOk;
    }

    public void Dispose()
    {
        // Streams must be closed before cleaning the output directory, otherwise the files are locked on Windows and cannot be deleted
        foreach (var stream in _openStreams)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception ex)
            {
                Errors.Add($"Failed to close the output stream: {ex.Message}");
            }
        }
    }
}
