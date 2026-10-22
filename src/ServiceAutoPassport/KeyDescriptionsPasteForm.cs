using System.Drawing;
using System.Windows.Forms;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class KeyDescriptionsPasteForm : Form
{
    private readonly TextBox _input = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 10F)
    };

    public IReadOnlyDictionary<string, string> Descriptions { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public KeyDescriptionsPasteForm()
    {
        Text = "Вставить описания из Excel";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 320);
        ClientSize = new Size(720, 430);
        ShowInTaskbar = false;

        var import = new Button { Text = "Добавить пары", AutoSize = true };
        import.Click += (_, _) =>
        {
            try
            {
                Descriptions = KeyDescriptionImport.Parse(_input.Text);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (InvalidDataException error)
            {
                MessageBox.Show(this, error.Message, "Ошибка в списке",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _input.Focus();
            }
        };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(import);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Скопируйте в Excel две колонки: ключ и описание. Вставьте их сюда (Ctrl+V). " +
                   "Между ключом и описанием можно поставить табуляцию или пробел."
        }, 0, 0);
        layout.Controls.Add(_input, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = import;
        CancelButton = cancel;
        Shown += (_, _) => _input.Focus();
    }
}
