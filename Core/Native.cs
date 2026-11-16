using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace pxdSpaceAnalyzer.Core;

/// <summary>Windows calls: fast directory listing, allocated sizes, disk space, the Recycle Bin and shell deletes.</summary>
internal static class Native
{
    // ---------- Directory listing ----------

    public const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const int FILE_ATTRIBUTE_SPARSE_FILE = 0x200;
    public const int FILE_ATTRIBUTE_COMPRESSED = 0x800;
    public const int FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FIND_FIRST_EX_LARGE_FETCH = 2;

    public const int ERROR_ACCESS_DENIED = 5;

    // FILETIMEs are two DWORDs (4-byte aligned): Pack = 4 keeps the longs where Windows puts them.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    public struct WIN32_FIND_DATA
    {
        public int dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    public sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstFileEx(string fileName, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll", EntryPoint = "FindNextFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFile(FindHandle h, out WIN32_FIND_DATA data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr h);

    /// <summary>Starts listing <paramref name="dir"/> (FindExInfoBasic, large fetch). Null with the Windows error when it can't.</summary>
    public static FindHandle? FindFirst(string dir, out WIN32_FIND_DATA data, out int error)
    {
        var h = FindFirstFileEx(Long(dir.TrimEnd('\\') + @"\*"), FindExInfoBasic, out data, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
        if (h.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            h.Dispose();
            return null;
        }

        error = 0;
        return h;
    }

    /// <summary>The first match of a full pattern (a file name or a wildcard), for files Windows won't open, like hiberfil.sys.</summary>
    public static FindHandle? FindPattern(string pattern, out WIN32_FIND_DATA data)
    {
        var h = FindFirstFileEx(Long(pattern), FindExInfoBasic, out data, FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    /// <summary>The \\?\ form, so paths longer than 260 characters work.</summary>
    public static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    // ---------- Sizes ----------

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string fileName, out uint high);

    /// <summary>The space a compressed, sparse or cloud file really takes; null when it can't be read.</summary>
    public static long? CompressedSize(string path)
    {
        var low = GetCompressedFileSize(Long(path), out var high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return null;
        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    /// <summary>The cluster size of the volume <paramref name="path"/> is on (4096 when it can't be read).</summary>
    public static long ClusterSize(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is { Length: > 0 } && GetDiskFreeSpace(root.EndsWith('\\') ? root : root + "\\", out var spc, out var bps, out _, out _) && spc > 0 && bps > 0)
                return (long)spc * bps;
        }
        catch
        {
            /* default below */
        }

        return 4096;
    }

    // ---------- Recycle Bin ----------

    [StructLayout(LayoutKind.Sequential, Pack = 0)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    /// <summary>What the Recycle Bin on <paramref name="driveRoot"/> holds (bytes, items), as Explorer counts it.</summary>
    public static (long Size, long Items)? QueryRecycleBin(string driveRoot)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            return SHQueryRecycleBin(driveRoot, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Empties the Recycle Bin on <paramref name="driveRoot"/> with no windows or sound. True when it worked (or was empty).</summary>
    public static bool EmptyRecycleBin(string driveRoot)
    {
        const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
        try
        {
            var r = SHEmptyRecycleBin(IntPtr.Zero, driveRoot, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return r == 0 || r == unchecked((int)0x8000FFFF);   // E_UNEXPECTED: it was already empty
        }
        catch
        {
            return false;
        }
    }

    // ---------- Shell delete ----------

    // Natural alignment: on 64-bit Windows this structure is not packed.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT64
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT64 op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_NOCONFIRMMKDIR = 0x200;

    /// <summary>Deletes a file or folder through the shell (to the Recycle Bin when asked), with no windows. True when it's gone.</summary>
    public static bool ShellDelete(string path, bool toRecycleBin)
    {
        try
        {
            var op = new SHFILEOPSTRUCT64
            {
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = (ushort)((toRecycleBin ? FOF_ALLOWUNDO : 0) | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR),
            };
            var r = SHFileOperation(ref op);
            return r == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    // ---------- Signed-in user (when running as SYSTEM) ----------

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    private static string? SessionString(uint session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buf, out _)) return null;
        try
        {
            return Marshal.PtrToStringUni(buf);
        }
        finally
        {
            WTSFreeMemory(buf);
        }
    }

    /// <summary>DOMAIN\user of the person at the console, else of the first active (Remote Desktop) session; null when nobody is signed in.</summary>
    public static string? SignedInUser()
    {
        const int WTSUserName = 5, WTSDomainName = 7, WTSActive = 0;
        try
        {
            string? Who(uint s)
            {
                var u = SessionString(s, WTSUserName);
                if (string.IsNullOrEmpty(u)) return null;
                var d = SessionString(s, WTSDomainName);
                return string.IsNullOrEmpty(d) ? u : d + "\\" + u;
            }

            var console = WTSGetActiveConsoleSessionId();
            if (console != 0xFFFFFFFF && Who(console) is { } c) return c;
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count)) return null;
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(list + i * size);
                    if (info.State == WTSActive && Who(info.SessionId) is { } u) return u;
                }
            }
            finally
            {
                WTSFreeMemory(list);
            }
        }
        catch
        {
            /* no one */
        }

        return null;
    }

    // ---------- Console ----------

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);
}
