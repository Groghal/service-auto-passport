using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ServiceAutoPassport;

internal static class WindowsClipboard
{
    private const int MaxHtmlBytes = 64 * 1024 * 1024;

    public static string ReadHtml()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return ReadHtmlOnce();
            }
            catch (ExternalException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }

        throw new ExternalException("Буфер обмена занят другим приложением.");
    }

    public static string ReadHtmlOnce()
    {
        var data = Clipboard.GetDataObject();
        if (data is null || !data.GetDataPresent(DataFormats.Html, false))
            throw new InvalidDataException("Буфер обмена не содержит HTML. Скопируйте полную страницу из Confluence.");

        return data.GetData(DataFormats.Html, false) switch
        {
            string html => ValidateHtml(html),
            MemoryStream stream => DecodeHtml(stream.ToArray()),
            byte[] bytes => DecodeHtml(bytes),
            _ => throw new InvalidDataException("Не удалось прочитать HTML из буфера обмена.")
        };
    }

    public static void Write(string html, string text)
    {
        var data = new DataObject();
        // CF_HTML offsets count UTF-8 bytes; keep the payload and its terminator byte-for-byte.
        data.SetData(DataFormats.Html, false, new MemoryStream(Encoding.UTF8.GetBytes(html + "\0"), writable: false));
        data.SetData(DataFormats.UnicodeText, false, text);
        Clipboard.SetDataObject(data, true, 10, 50);
    }

    private static string ValidateHtml(string html)
    {
        if (html.Length == 0 || Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
            throw new InvalidDataException("Размер HTML в буфере обмена недопустим.");

        return html.TrimEnd('\0');
    }

    private static string DecodeHtml(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxHtmlBytes)
            throw new InvalidDataException("Размер HTML в буфере обмена недопустим.");

        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
}
