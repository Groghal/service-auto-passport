using System.Drawing;
using System.Windows.Forms;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class HtmlDescriptionForm : Form
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

    public string Html { get; private set; } = "";

    public HtmlDescriptionForm(string currentHtml, string fieldName)
    {
        Text = $"HTML: {fieldName}";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 320);
        ClientSize = new Size(760, 450);
        ShowInTaskbar = false;
        _input.Text = currentHtml;

        var fromClipboard = new Button { Text = "Взять HTML из буфера", AutoSize = true };
        fromClipboard.Click += (_, _) =>
        {
            try { _input.Text = CfHtml.ExtractFragment(WindowsClipboard.ReadHtml()).Trim(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Не удалось прочитать HTML из буфера",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var save = new Button { Text = "Применить HTML", AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                Html = DescriptionHtml.Normalize(_input.Text);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (ArgumentException)
            {
                MessageBox.Show(this, "Вставьте непустой HTML-фрагмент.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (InvalidDataException error)
            {
                MessageBox.Show(this, error.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        actions.Controls.Add(save);
        actions.Controls.Add(fromClipboard);

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
            Text = "Вставьте HTML-код или скопируйте фрагмент в Confluence и нажмите «Взять HTML из буфера». " +
                   "Его структура и атрибуты попадут в выбранное поле."
        }, 0, 0);
        layout.Controls.Add(_input, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => _input.Focus();
    }
}
