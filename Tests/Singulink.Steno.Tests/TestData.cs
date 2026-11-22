using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Singulink.Steno.Tests;

/// <summary>
/// The data the tests read from files, and the conversions between UTF-16 text and code points. Nothing here uses the library.
/// </summary>
internal static partial class TestData
{
    private static readonly Lazy<IReadOnlyList<Vector>> _vectors = new(LoadVectors);
    private static readonly Lazy<IReadOnlyList<(string Name, string Text)>> _corpus = new(LoadCorpus);

    /// <summary>
    /// Gets the vectors of TestVectors/steno-v1-vectors.txt.
    /// </summary>
    public static IReadOnlyList<Vector> Vectors => _vectors.Value;

    /// <summary>
    /// Gets the files of the benchmark corpus, by name (folder/file), as they are.
    /// </summary>
    public static IReadOnlyList<(string Name, string Text)> Corpus => _corpus.Value;

    /// <summary>
    /// Gets the code points of UTF-16 text: a surrogate pair is one code point, a lone surrogate is a code point of its own.
    /// </summary>
    public static List<int> ToCodePoints(string text)
    {
        var codePoints = new List<int>(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoints.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                i++;
            }
            else
            {
                codePoints.Add(text[i]);
            }
        }

        return codePoints;
    }

    /// <summary>
    /// Gets the UTF-16 text of code points. A surrogate code point becomes the code unit of the same value.
    /// </summary>
    public static string ToText(IEnumerable<int> codePoints)
    {
        var text = new StringBuilder();

        foreach (int c in codePoints)
        {
            if (c < 0x10000)
                text.Append((char)c);
            else
                text.Append(char.ConvertFromUtf32(c));
        }

        return text.ToString();
    }

    /// <summary>
    /// Encodes text with the reference codec.
    /// </summary>
    public static byte[] ReferenceEncode(string text) => ReferenceSteno.Encode(ToCodePoints(text));

    /// <summary>
    /// Decodes bytes with the reference codec: the text, or the reason of the rejection.
    /// </summary>
    public static (string? Text, string? Reason) ReferenceDecode(byte[] bytes)
    {
        try
        {
            return (ToText(ReferenceSteno.Decode(bytes)), null);
        }
        catch (ReferenceStenoException ex)
        {
            return (null, ex.Reason);
        }
    }

    /// <summary>
    /// Decodes bytes with the library: the text, or the reason that the message of the rejection names.
    /// </summary>
    public static (string? Text, string? Reason) LibraryDecode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return (StenoEncoding.Instance.GetString(bytes), null);
        }
        catch (DecoderFallbackException ex)
        {
            return (null, ReasonOf(ex));
        }
    }

    /// <summary>
    /// Gets the reason that the message of a rejection names, in parentheses.
    /// </summary>
    public static string? ReasonOf(DecoderFallbackException ex)
    {
        var match = ReasonPattern().Match(ex.Message);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string Hex(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "-" : Convert.ToHexString(bytes);

    public static string Describe(string text)
    {
        var units = text.Take(40).Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture));
        return $"{text.Length} chars: {string.Join(' ', units)}{(text.Length > 40 ? " …" : string.Empty)}";
    }

    private static List<Vector> LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestVectors", "steno-v1-vectors.txt");
        var vectors = new List<Vector>();

        foreach (string line in File.ReadLines(path))
        {
            if (line.Length is 0 || line[0] is '#')
                continue;

            string[] fields = line.Split('\t');

            vectors.Add(fields[0] switch {
                "valid" => new Vector(fields[0], fields[1], ParseCodePoints(fields[2]), ParseBytes(fields[3]), null),
                "invalid" => new Vector(fields[0], fields[1], [], ParseBytes(fields[2]), fields[3]),
                "not-a-value" => new Vector(fields[0], fields[1], ParseCodePoints(fields[2]), [], null),
                _ => throw new InvalidDataException($"Unknown vector kind '{fields[0]}'."),
            });
        }

        return vectors;
    }

    private static List<int> ParseCodePoints(string field) =>
        field is "-" ? [] : [.. field.Split(' ').Select(c => int.Parse(c, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];

    private static byte[] ParseBytes(string field) => field is "-" ? [] : Convert.FromHexString(field);

    private static List<(string Name, string Text)> LoadCorpus()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Corpus");

        return [.. Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f, Encoding.UTF8)))];
    }

    [GeneratedRegex(@"\(([a-z-]+)\)")]
    private static partial Regex ReasonPattern();
}

/// <summary>
/// A vector of the vector file (FORMAT.md section 12): <c>valid</c>, <c>invalid</c> or <c>not-a-value</c>.
/// </summary>
internal sealed record Vector(string Kind, string Name, IReadOnlyList<int> CodePoints, byte[] Bytes, string? Reason);
