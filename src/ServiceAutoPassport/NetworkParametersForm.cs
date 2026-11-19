using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class NetworkParametersForm : Form
{
    private readonly Dictionary<string, NetworkTargetSettings> _parameterSources;
    private readonly Action<IReadOnlyDictionary<string, NetworkParameterEdit>> _save;
    private readonly Func<IReadOnlyDictionary<string, NetworkParameterEdit>, NetworkTemplatePrefillResult> _prefill;
    private readonly Dictionary<string, NetworkParameterEdit> _draft = new(StringComparer.Ordinal);

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };

    private readonly Label _hint = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    public NetworkParametersForm(IReadOnlyDictionary<string, NetworkTargetSettings> targets,
        IReadOnlyList<NetworkConnection> connections,
        Action<IReadOnlyDictionary<string, NetworkParameterEdit>> save,
        Func<IReadOnlyDictionary<string, NetworkParameterEdit>, NetworkTemplatePrefillResult> prefill)
    {
        _parameterSources = new(targets, StringComparer.Ordinal);
        foreach (var connection in connections)
            _parameterSources.TryAdd(connection.TargetKey, connection.ExternalTarget);
        _save = save;
        _prefill = prefill;
        Text = "Параметры сети";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(820, 340);
        ClientSize = new Size(980, 480);
        ShowInTaskbar = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Найденный параметр", ReadOnly = true, Width = 320, SortMode = DataGridViewColumnSortMode.Automatic,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Название элемента", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 140,
            SortMode = DataGridViewColumnSortMode.Automatic
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Блок", Width = 150, SortMode = DataGridViewColumnSortMode.Automatic
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Блок-родитель", Width = 150, SortMode = DataGridViewColumnSortMode.Automatic,
            ToolTipText = "Внешний контейнер для указанного блока. Если «Блок» пуст, узел помещается прямо в блок-родитель."
        });

        var parameters = targets.Select(pair => (pair.Key, Caption: NetworkParameterCatalog.DisplayParameter(pair.Key, pair.Value), pair.Value));
        foreach (var parameter in parameters.OrderBy(p => p.Caption, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal))
            AddRow(parameter.Key, parameter.Caption, new NetworkParameterEdit(parameter.Value.Label, parameter.Value.Block)
            {
                ParentBlock = parameter.Value.ParentBlock,
                LabelIsAutomatic = parameter.Value.LabelIsAutomatic,
                BlockIsAutomatic = parameter.Value.BlockIsAutomatic
            });
        _hint.Text = targets.Count == 0
            ? "Список пополняется после анализа. Можно заранее добавить параметры из Excel."
            : "Названия и блоки применяются к внешним узлам. Для отдельного узла очистите «Блок» и «Блок-родитель».";

        var paste = new Button { Text = "Вставить из Excel…", AutoSize = true };
        paste.Click += (_, _) => Import();
        var fromTemplate = new Button { Text = "Предзаполнить из шаблона", AutoSize = true };
        fromTemplate.Click += (_, _) => ImportTemplate();
        var delete = new Button { Text = "Удалить выбранные", AutoSize = true, Enabled = _grid.SelectedRows.Count > 0 };
        _grid.SelectionChanged += (_, _) => delete.Enabled = _grid.SelectedRows.Count > 0;
        delete.Click += (_, _) => DeleteSelected();
        var saveButton = new Button { Text = "Сохранить", AutoSize = true };
        saveButton.Click += (_, _) => SaveEdits();
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true
        };
        actions.Controls.AddRange([cancel, saveButton, delete, paste, fromTemplate]);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.Controls.Add(_hint, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = saveButton;
        CancelButton = cancel;
    }

    private void AddRow(string key, string caption, NetworkParameterEdit edit)
    {
        _draft.Add(key, edit);
        _grid.Rows[_grid.Rows.Add(caption, edit.Label, edit.Block, edit.ParentBlock)].Tag = key;
    }

    private void Import()
    {
        if (!_grid.EndEdit()) return;
        ReadDraft();
        using var dialog = new TablePasteForm<IReadOnlyDictionary<string, NetworkParameterEdit>>("Вставить параметры из Excel",
            "Скопируйте колонки «Найденный параметр», «Название элемента», «Блок» и необязательную колонку «Блок-родитель». " +
            "Вставьте их сюда (Ctrl+V). Пустая четвёртая ячейка очищает родителя.",
            "Вставить", text => NetworkParameterCatalog.ParseExcel(text, _parameterSources, _draft));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var (added, updated) = ApplyEdits(dialog.Result!);
        _hint.Text = $"Добавлено {added}, обновлено {updated}. Нажмите «Сохранить», чтобы применить правки.";
    }

    private void ImportTemplate()
    {
        if (!_grid.EndEdit()) return;
        ReadDraft();
        try
        {
            var result = _prefill(_draft);
            if (result.Edits.Count == 0 && result.Skipped.Count == 0)
            {
                _hint.Text = "Нет строк для предзаполнения. Скопируйте заполненный Table Transformer " +
                             "из редактора Confluence и нажмите кнопку снова.";
                return;
            }

            var (added, updated) = ApplyEdits(result.Edits);
            _hint.Text = $"{result.Source}. Добавлено {added}, обновлено {updated}, пропущено {result.Skipped.Count}. " +
                         "Нажмите «Сохранить».";
            if (result.Skipped.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Skipped.Take(12));
                if (result.Skipped.Count > 12) details += $"\nЕщё пропущено {result.Skipped.Count - 12}.";
                MessageBox.Show(this, details, "Пропущенные строки шаблона", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (InvalidDataException error)
        {
            MessageBox.Show(this, error.Message, "Предзаполнение из шаблона", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteSelected()
    {
        if (!_grid.EndEdit()) return;
        var rows = _grid.SelectedRows.Cast<DataGridViewRow>().ToArray();
        foreach (var row in rows)
        {
            _draft.Remove((string)row.Tag!);
            _grid.Rows.Remove(row);
        }

        _hint.Text = $"Удалено строк: {rows.Length}. Можно импортировать их заново. " +
                     "Чтобы применить правки, нажмите «Сохранить».";
    }

    private (int Added, int Updated) ApplyEdits(IReadOnlyDictionary<string, NetworkParameterEdit> edits)
    {
        var rows = _grid.Rows.Cast<DataGridViewRow>().ToDictionary(row => (string)row.Tag!, StringComparer.Ordinal);
        var updated = 0;
        foreach (var (key, edit) in edits)
        {
            if (rows.TryGetValue(key, out var row))
            {
                _draft[key] = edit;
                row.Cells[1].Value = edit.Label;
                row.Cells[2].Value = edit.Block;
                row.Cells[3].Value = edit.ParentBlock;
                updated++;
            }
            else
            {
                var source = _parameterSources.GetValueOrDefault(key) ?? new NetworkTargetSettings();
                AddRow(key, NetworkParameterCatalog.DisplayParameter(key, source), edit);
            }
        }
        return (edits.Count - updated, updated);
    }

    private void ReadDraft()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var key = (string)row.Tag!;
            var previous = _draft[key];
            var label = row.Cells[1].Value?.ToString()?.Trim() ?? "";
            var block = row.Cells[2].Value?.ToString()?.Trim() ?? "";
            var parent = row.Cells[3].Value?.ToString()?.Trim() ?? "";
            _draft[key] = new NetworkParameterEdit(label, block)
            {
                ParentBlock = parent.Length == 0 && previous.ParentBlock is null ? null : parent,
                LabelIsAutomatic = previous.LabelIsAutomatic && label == previous.Label,
                BlockIsAutomatic = previous.BlockIsAutomatic && block == previous.Block
            };
        }
    }

    private void SaveEdits()
    {
        if (!_grid.EndEdit()) return;
        ReadDraft();
        var missingName = _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => _draft[(string)row.Tag!].Label.Length == 0);
        if (missingName is not null)
        {
            MessageBox.Show(this, "Укажите название элемента.", "Проверьте строку", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _grid.CurrentCell = missingName.Cells[1];
            return;
        }

        try { _save(_draft); }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Не удалось сохранить параметры", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
