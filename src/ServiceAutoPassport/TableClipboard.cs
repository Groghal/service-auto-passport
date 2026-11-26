using System.Runtime.InteropServices;

namespace ServiceAutoPassport;

internal static class TableClipboard
{
    public static void Copy(Form owner, Label hint, Func<string> export, int rows)
    {
        if (rows == 0)
        {
            hint.Text = "В таблице нет строк для копирования.";
            return;
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, false, export());
            Clipboard.SetDataObject(data, true, 10, 50);
            hint.Text = $"Скопировано строк: {rows}. Вставьте их в Excel или в окно импорта.";
        }
        catch (ExternalException)
        {
            MessageBox.Show(owner, "Буфер обмена занят. Повторите копирование.", "Копирование таблицы",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static bool? ReplaceMatches(Form owner, int matches)
    {
        var choice = MessageBox.Show(owner,
            $"Совпадающих строк: {matches}. Заменить их значениями из импорта?\n\n" +
            "Да — заменить совпадения. Нет — оставить текущие значения. Новые строки добавятся в обоих случаях.",
            "Совпадения при импорте", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        return choice == DialogResult.Cancel ? null : choice == DialogResult.Yes;
    }
}
