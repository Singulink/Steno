using System.Runtime.InteropServices;

namespace Singulink.Steno.Tests;

/// <summary>
/// A page of memory between two pages that cannot be touched, so that a read or write just outside a span placed at either end of the page crashes the
/// test run instead of passing unseen. Available on Windows and Linux.
/// </summary>
internal sealed unsafe partial class GuardedMemory : IDisposable
{
    private readonly byte* _base;
    private readonly nuint _size;

    public GuardedMemory()
    {
        PageSize = Environment.SystemPageSize;
        _size = (nuint)(PageSize * 3);

        if (OperatingSystem.IsWindows())
        {
            _base = (byte*)Windows.VirtualAlloc(null, _size, Windows.MemCommit | Windows.MemReserve, Windows.PageReadWrite);

            if (_base is null || !Windows.VirtualProtect(_base, (nuint)PageSize, Windows.PageNoAccess, out _) ||
                !Windows.VirtualProtect(_base + (PageSize * 2), (nuint)PageSize, Windows.PageNoAccess, out _))
            {
                throw new InvalidOperationException($"Could not set up the guard pages ({Marshal.GetLastPInvokeError()}).");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            _base = (byte*)Linux.Mmap(null, _size, Linux.ProtRead | Linux.ProtWrite, Linux.MapPrivate | Linux.MapAnonymous, -1, 0);

            if (_base == (byte*)-1 || Linux.Mprotect(_base, (nuint)PageSize, Linux.ProtNone) != 0 ||
                Linux.Mprotect(_base + (PageSize * 2), (nuint)PageSize, Linux.ProtNone) != 0)
            {
                throw new InvalidOperationException($"Could not set up the guard pages ({Marshal.GetLastPInvokeError()}).");
            }
        }
        else
        {
            throw new PlatformNotSupportedException();
        }
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public int PageSize { get; }

    /// <summary>
    /// Gets a span of the given length that starts where the usable page starts.
    /// </summary>
    public Span<T> AtStart<T>(int count)
        where T : unmanaged
    {
        if (count * sizeof(T) > PageSize)
            throw new ArgumentOutOfRangeException(nameof(count));

        return new Span<T>(_base + PageSize, count);
    }

    /// <summary>
    /// Gets a span of the given length that ends where the usable page ends.
    /// </summary>
    public Span<T> AtEnd<T>(int count)
        where T : unmanaged
    {
        if (count * sizeof(T) > PageSize)
            throw new ArgumentOutOfRangeException(nameof(count));

        return new Span<T>(_base + (PageSize * 2) - (count * sizeof(T)), count);
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            Windows.VirtualFree(_base, 0, Windows.MemRelease);
        else
            Linux.Munmap(_base, _size);
    }

    private static partial class Windows
    {
        public const uint MemCommit = 0x1000;
        public const uint MemReserve = 0x2000;
        public const uint MemRelease = 0x8000;
        public const uint PageNoAccess = 0x01;
        public const uint PageReadWrite = 0x04;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool VirtualFree(void* address, nuint size, uint freeType);
    }

    private static partial class Linux
    {
        public const int ProtNone = 0;
        public const int ProtRead = 1;
        public const int ProtWrite = 2;
        public const int MapPrivate = 0x02;
        public const int MapAnonymous = 0x20;

        [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
        public static partial void* Mmap(void* address, nuint length, int protection, int flags, int fd, nint offset);

        [LibraryImport("libc", EntryPoint = "mprotect", SetLastError = true)]
        public static partial int Mprotect(void* address, nuint length, int protection);

        [LibraryImport("libc", EntryPoint = "munmap", SetLastError = true)]
        public static partial int Munmap(void* address, nuint length);
    }
}
