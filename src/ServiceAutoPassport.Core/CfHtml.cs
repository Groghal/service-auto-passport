using System.Text;
using System.Text.RegularExpressions;

namespace ServiceAutoPassport.Core;

/// <summary>Reads and writes the Windows "HTML Format" clipboard payload.</summary>
public static partial class CfHtml
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    public static string ExtractFragment(string clipboardHtml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipboardHtml);

        // Exported clipboard dumps can acquire a BOM, making their recorded byte
        // offsets stale. The markers are the most reliable way to read those dumps.
        var start = clipboardHtml.IndexOf(StartMarker, StringComparison.OrdinalIgnoreCase);
        var end = clipboardHtml.IndexOf(EndMarker, StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start)
        {
            start += StartMarker.Length;
            return clipboardHtml[start..end];
        }

        var bytes = Encoding.UTF8.GetBytes(clipboardHtml.TrimStart('\uFEFF'));
        var startMatch = StartFragmentRegex().Match(clipboardHtml);
        var endMatch = EndFragmentRegex().Match(clipboardHtml);
        if (startMatch.Success && endMatch.Success &&
            int.TryParse(startMatch.Groups[1].Value, out var startByte) &&
            int.TryParse(endMatch.Groups[1].Value, out var endByte) &&
            startByte >= 0 && endByte > startByte && endByte <= bytes.Length)
        {
            return Encoding.UTF8.GetString(bytes, startByte, endByte - startByte);
        }

        if (clipboardHtml.Contains('<'))
        {
            // Some applications put an ordinary HTML fragment in the same format.
            var firstTag = clipboardHtml.IndexOf('<');
            return clipboardHtml[firstTag..].TrimEnd('\0');
        }

        throw new InvalidDataException("Буфер обмена не содержит HTML страницы Confluence.");
    }

    public static string Build(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        const string prefix = "<html><body>" + StartMarker;
        const string suffix = EndMarker + "</body></html>";
        const string headerFormat = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n" +
                                    "StartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var placeholder = string.Format(headerFormat, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(placeholder);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(suffix);
        var header = string.Format(headerFormat, startHtml, endHtml, startFragment, endFragment);
        return header + prefix + fragment + suffix;
    }

    [GeneratedRegex(@"StartFragment:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex StartFragmentRegex();

    [GeneratedRegex(@"EndFragment:(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EndFragmentRegex();
}
