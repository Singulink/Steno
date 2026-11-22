using System.Runtime.InteropServices;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Reference encoders from ICU4C, called through the icu.dll that ships with Windows 10 1903+ / Windows 11.
/// Used as the apples-to-apples baseline: ICU's SCSU encoder (ucnvscsu.c) is the reference-quality implementation that ICU4J's CharsetSCSU was ported from.
/// </summary>
internal sealed unsafe partial class IcuConverter : IDisposable
{
    private const string IcuLibrary = "icu.dll";

    private nint _converter;

    public string Name { get; }

    public IcuConverter(string name)
    {
        Name = name;
        int error = 0;
        _converter = ucnv_open(name, ref error);

        if (error > 0 || _converter == 0)
            throw new InvalidOperationException($"ICU could not open converter '{name}' (UErrorCode {error}).");
    }

    public static bool IsAvailable
    {
        get {
            try
            {
                using var c = new IcuConverter("SCSU");
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static string Version
    {
        get {
            byte* v = stackalloc byte[4];
            u_getVersion(v);
            return $"{v[0]}.{v[1]}";
        }
    }

    public int GetMaxByteCount(int charCount) => (charCount * 4) + 16;

    public int Encode(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        fixed (char* pChars = chars)
        fixed (byte* pBytes = bytes)
        {
            int error = 0;
            int length = ucnv_fromUChars(_converter, pBytes, bytes.Length, pChars, chars.Length, ref error);

            if (error > 0)
                throw new InvalidOperationException($"ICU {Name} encode failed (UErrorCode {error}).");

            return length;
        }
    }

    public byte[] Encode(ReadOnlySpan<char> chars)
    {
        byte[] buffer = new byte[GetMaxByteCount(chars.Length)];
        int length = Encode(chars, buffer);
        return buffer.AsSpan(0, length).ToArray();
    }

    public int Decode(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        fixed (byte* pBytes = bytes)
        fixed (char* pChars = chars)
        {
            int error = 0;
            int length = ucnv_toUChars(_converter, pChars, chars.Length, pBytes, bytes.Length, ref error);

            if (error > 0)
                throw new InvalidOperationException($"ICU {Name} decode failed (UErrorCode {error}).");

            return length;
        }
    }

    public string Decode(ReadOnlySpan<byte> bytes)
    {
        char[] buffer = new char[bytes.Length + 16];
        int length = Decode(bytes, buffer);
        return new string(buffer, 0, length);
    }

    public void Dispose()
    {
        if (_converter != 0)
        {
            ucnv_close(_converter);
            _converter = 0;
        }
    }

#pragma warning disable SA1300 // Element should begin with an upper-case letter: the imports keep the names of ICU's C API.

    [LibraryImport(IcuLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ucnv_open(string converterName, ref int errorCode);

    [LibraryImport(IcuLibrary)]
    private static partial void ucnv_close(nint converter);

    [LibraryImport(IcuLibrary)]
    private static partial int ucnv_fromUChars(nint converter, byte* dest, int destCapacity, char* src, int srcLength, ref int errorCode);

    [LibraryImport(IcuLibrary)]
    private static partial int ucnv_toUChars(nint converter, char* dest, int destCapacity, byte* src, int srcLength, ref int errorCode);

    [LibraryImport(IcuLibrary)]
    private static partial void u_getVersion(byte* versionArray);

#pragma warning restore SA1300
}
