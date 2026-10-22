using System.Drawing;
using System.Windows.Forms;

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
        Text = "Укажите имя переменной J2 и описание. Можно вставить сразу две колонки из Excel."
    };

    public Dictionary<string, string> Descriptions { get; private set; } = new(StringComparer.Ordinal);

    public KeyDescriptionsForm(IReadOnlyDictionary<string, string> descriptions)
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
        foreach (var pair in descriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            _grid.Rows.Add(pair.Key, pair.Value);

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
        var save = new Button { Text = "Сохранить", AutoSize = true };
        save.Click += (_, _) => SaveDescriptions();
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        actions.Controls.Add(delete);
        actions.Controls.Add(add);
        actions.Controls.Add(paste);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(_hint, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void ImportDescriptions()
    {
        using var dialog = new KeyDescriptionsPasteForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _grid.EndEdit();
        var updated = 0;
        foreach (var pair in dialog.Descriptions)
        {
            var existing = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row =>
                !row.IsNewRow && string.Equals(row.Cells[0].Value?.ToString()?.Trim(),
                    pair.Key, StringComparison.Ordinal));
            if (existing is null)
                _grid.Rows.Add(pair.Key, pair.Value);
            else
            {
                existing.Cells[1].Value = pair.Value;
                updated++;
            }
        }

        _hint.Text = $"Добавлено: {dialog.Descriptions.Count - updated}. Обновлено: {updated}. " +
                     "Нажмите «Сохранить», чтобы записать настройки.";
    }

    private void SaveDescriptions()
    {
        _grid.EndEdit();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
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
                return;
            }

            if (!result.TryAdd(key, description))
            {
                MessageBox.Show(this, $"Ключ «{key}» указан несколько раз.",
                    "Проверьте ключи", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        Descriptions = result;
        DialogResult = DialogResult.OK;
        Close();
    }
}
