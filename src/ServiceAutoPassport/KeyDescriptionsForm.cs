using System.Drawing;
using System.Windows.Forms;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class KeyDescriptionsForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    private readonly Label _hint = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Укажите ключ и описание. Для HTML отметьте колонку «HTML» или откройте редактор."
    };

    public Dictionary<string, string> Descriptions { get; private set; } = new(StringComparer.Ordinal);
    public HashSet<string> HtmlKeys { get; private set; } = new(StringComparer.Ordinal);

    public KeyDescriptionsForm(IReadOnlyDictionary<string, string> descriptions,
        IReadOnlySet<string> htmlDescriptionKeys)
    {
        Text = "Описания ключей";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(580, 340);
        ClientSize = new Size(760, 480);
        ShowInTaskbar = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Ключ из J2",
            Width = 250,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Описание",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "HTML",
            Width = 66,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        foreach (var pair in descriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            _grid.Rows.Add(pair.Key, pair.Value, htmlDescriptionKeys.Contains(pair.Key));

        var add = new Button { Text = "Добавить", AutoSize = true };
        add.Click += (_, _) =>
        {
            var row = _grid.Rows[_grid.Rows.Add()];
            _grid.CurrentCell = row.Cells[0];
            _grid.BeginEdit(true);
        };
        var delete = new Button { Text = "Удалить выбранное", AutoSize = true };
        delete.Click += (_, _) =>
        {
            foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).ToArray())
                _grid.Rows.Remove(row);
        };
        var paste = new Button { Text = "Вставить из Excel…", AutoSize = true };
        paste.Click += (_, _) => ImportDescriptions();
        var copy = new Button { Text = "Скопировать все строки", AutoSize = true };
        copy.Click += (_, _) =>
        {
            var entries = ReadDescriptions();
            if (entries is not null)
                TableClipboard.Copy(this, _hint, () => KeyDescriptionImport.Export(entries), entries.Count);
        };
        var editHtml = new Button { Text = "Редактор HTML…", AutoSize = true };
        editHtml.Click += (_, _) => EditSelectedHtml();
        var save = new Button { Text = "Сохранить", AutoSize = true };
        save.Click += (_, _) => SaveDescriptions();
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        actions.Controls.Add(delete);
        actions.Controls.Add(add);
        actions.Controls.Add(paste);
        actions.Controls.Add(copy);
        actions.Controls.Add(editHtml);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.Controls.Add(_hint, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void ImportDescriptions()
    {
        using var dialog = new TablePasteForm<IReadOnlyDictionary<string, KeyDescriptionEntry>>("Вставить описания из Excel",
            "Вставьте экспорт приложения или пары «ключ — описание» из Excel (Ctrl+V). " +
            "Для пар подойдёт табуляция или пробел.",
            "Импортировать", KeyDescriptionImport.ParseEntries);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (!_grid.EndEdit()) return;
        var imported = dialog.Result!;
        try
        {
            foreach (var entry in imported.Values.Where(entry => entry.IsHtml))
                DescriptionHtml.Normalize(entry.Description);
        }
        catch (InvalidDataException error)
        {
            MessageBox.Show(this, error.Message, "Ошибка в HTML описания",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existing = _grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow)
            .ToLookup(row => row.Cells[0].Value?.ToString()?.Trim() ?? "", StringComparer.Ordinal);
        var changes = SettingsTableImport.SelectChanges(imported, existing.Contains, count => TableClipboard.ReplaceMatches(this, count));
        if (changes is null) return;

        var added = 0;
        var updated = 0;
        foreach (var (key, entry) in changes)
        {
            var row = existing[key].FirstOrDefault();
            if (row is null)
            {
                _grid.Rows.Add(key, entry.Description, entry.IsHtml);
                added++;
            }
            else
            {
                row.Cells[1].Value = entry.Description;
                row.Cells[2].Value = entry.IsHtml;
                updated++;
            }
        }

        _hint.Text = $"Добавлено: {added}. Обновлено: {updated}. " +
                     "Нажмите «Сохранить», чтобы записать настройки.";
    }

    private void EditSelectedHtml()
    {
        _grid.EndEdit();
        var row = _grid.SelectedRows.Cast<DataGridViewRow>().FirstOrDefault(row => !row.IsNewRow);
        if (row is null)
        {
            MessageBox.Show(this, "Выберите строку с ключом.", "HTML описания",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new HtmlDescriptionForm(row.Cells[1].Value?.ToString() ?? "", "описание ключа");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        row.Cells[1].Value = dialog.Html;
        row.Cells[2].Value = true;
    }

    private Dictionary<string, KeyDescriptionEntry>? ReadDescriptions()
    {
        if (!_grid.EndEdit()) return null;
        var result = new Dictionary<string, KeyDescriptionEntry>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            var key = row.Cells[0].Value?.ToString()?.Trim() ?? "";
            var description = row.Cells[1].Value?.ToString()?.Trim() ?? "";
            if (key.Length == 0 && description.Length == 0) continue;
            if (key.Length == 0 || description.Length == 0)
            {
                MessageBox.Show(this, "У каждой пары должны быть ключ и описание.",
                    "Проверьте строку", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            if (result.ContainsKey(key))
            {
                MessageBox.Show(this, $"Ключ «{key}» указан несколько раз.",
                    "Проверьте ключи", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }

            var isHtml = row.Cells[2].Value is true;
            if (isHtml)
            {
                try { description = DescriptionHtml.Normalize(description); }
                catch (InvalidDataException error)
                {
                    MessageBox.Show(this, $"Ключ «{key}»: {error.Message}",
                        "Проверьте HTML", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }
            }
            result.Add(key, new KeyDescriptionEntry(description, isHtml));
        }
        return result;
    }

    private void SaveDescriptions()
    {
        var entries = ReadDescriptions();
        if (entries is null) return;
        Descriptions = entries.ToDictionary(pair => pair.Key, pair => pair.Value.Description, StringComparer.Ordinal);
        HtmlKeys = entries.Where(pair => pair.Value.IsHtml).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        DialogResult = DialogResult.OK;
        Close();
    }
}
