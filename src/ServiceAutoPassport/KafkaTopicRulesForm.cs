using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class KafkaTopicRulesForm : Form
{
    private sealed record DirectionOption(KafkaTopicDirection Direction, string Label);

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    public List<KafkaTopicRule> Rules { get; private set; } = [];

    public KafkaTopicRulesForm(IReadOnlyList<KafkaTopicRule> rules)
    {
        Text = "Правила топиков Kafka";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(530, 320);
        ClientSize = new Size(720, 420);
        ShowInTaskbar = false;

        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Направление",
            Width = 135,
            DataSource = new[] { new DirectionOption(KafkaTopicDirection.Read, "Чтение"), new DirectionOption(KafkaTopicDirection.Write, "Запись") },
            DisplayMember = nameof(DirectionOption.Label),
            ValueMember = nameof(DirectionOption.Direction),
            ValueType = typeof(KafkaTopicDirection)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Путь", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        foreach (var rule in rules)
        {
            var direction = rule?.Direction;
            _grid.Rows.Add(direction is KafkaTopicDirection.Read or KafkaTopicDirection.Write ? direction : null, rule?.Path);
        }

        var add = new Button { Text = "Добавить", AutoSize = true };
        add.Click += (_, _) =>
        {
            _grid.CurrentCell = _grid.Rows[_grid.Rows.Add(KafkaTopicDirection.Read, "")].Cells[1];
            _grid.BeginEdit(true);
        };
        var delete = new Button { Text = "Удалить выбранные", AutoSize = true };
        delete.Click += (_, _) =>
        {
            if (!_grid.EndEdit()) return;
            foreach (var row in _grid.SelectedRows.Cast<DataGridViewRow>().ToArray()) _grid.Rows.Remove(row);
        };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        save.Click += (_, _) => SaveRules();
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange(new Control[] { cancel, save, delete, add });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Путь начинается от корня JSON. * — произвольный ключ, {kafka} — раздел кластера, [] — элементы массива. " +
                "Например, *.{kafka}.Producers[].TopicId."
        }, 0, 0);
        layout.Controls.Add(_grid, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void SaveRules()
    {
        if (!_grid.EndEdit()) return;
        var rules = _grid.Rows.Cast<DataGridViewRow>().Select(row => new KafkaTopicRule(
            row.Cells[0].Value is KafkaTopicDirection direction ? direction : 0,
            row.Cells[1].Value?.ToString()?.Trim() ?? "")).ToList();
        try { KafkaTopicReport.ValidateRules(rules); }
        catch (InvalidDataException error)
        {
            MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Rules = rules;
        DialogResult = DialogResult.OK;
    }
}
