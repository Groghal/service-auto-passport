using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class MainForm : Form
{
    private readonly LocalSettings _settings;
    private readonly TextBox _appParamsRoot = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serviceFolder = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _service = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _template = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _displayName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _team = new() { Dock = DockStyle.Fill };
    private readonly Label _clipboardIndicator = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(8, 0, 8, 0)
    };
    private readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 2000 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _saveTemplate = new() { Text = "Сохранить буфер как шаблон", Dock = DockStyle.Fill };
    private readonly Button _editKeyDescriptions = new() { Text = "Описания ключей…", Dock = DockStyle.Fill };
    private readonly Button _generateCurrent = new() { Text = "Заполнить шаблон из буфера", Dock = DockStyle.Fill };
    private readonly Button _generateFromDefault = new() { Text = "Сохранённый шаблон + буфер", Dock = DockStyle.Fill };

    private string? _activeService;
    private string? _lastCheckedClipboardHtml;
    private string? _generatedClipboardHtml;
    private bool _refreshing;

    public MainForm()
    {
        _settings = LocalSettings.Load();
        Text = "Паспорт сервиса для Confluence";
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Font;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 590);
        ClientSize = new Size(940, 610);

        BuildLayout();
        SetClipboardIndicator("Проверяем буфер обмена…", Color.DimGray);
        _status.Text = string.IsNullOrWhiteSpace(_settings.DefaultTemplateHtml)
            ? "Шаблон по умолчанию не сохранён. Скопируйте полную страницу из Confluence."
            : "Шаблон по умолчанию сохранён. Можно заполнить его значениями из буфера.";
        _generateFromDefault.Enabled = !string.IsNullOrWhiteSpace(_settings.DefaultTemplateHtml);
        _appParamsRoot.Text = _settings.AppParamsRoot;
        _serviceFolder.Text = _settings.ServiceFolder;
        _service.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshing || _service.SelectedIndex < 0) return;
            CaptureIdentity();
            LoadIdentity();
        };
        _service.Leave += (_, _) =>
        {
            if (_refreshing || _activeService == CurrentService) return;
            CaptureIdentity();
            LoadIdentity();
        };
        _appParamsRoot.Leave += (_, _) => TryRefresh(() => RefreshServices(preferFolder: true));
        _serviceFolder.Leave += (_, _) => TryRefresh(RefreshForServiceFolder);
        _saveTemplate.Click += SaveDefaultTemplate;
        _editKeyDescriptions.Click += EditKeyDescriptions;
        _generateCurrent.Click += (_, _) => GeneratePassport(useDefaultTemplate: false);
        _generateFromDefault.Click += (_, _) => GeneratePassport(useDefaultTemplate: true);
        _clipboardTimer.Tick += (_, _) => RefreshClipboardIndicator();
        Shown += (_, _) =>
        {
            RefreshClipboardIndicator();
            _clipboardTimer.Start();
        };
        Activated += (_, _) => RefreshClipboardIndicator();
        FormClosed += (_, _) => _clipboardTimer.Dispose();
        FormClosing += (_, _) =>
        {
            try { SaveSettings(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Не удалось сохранить настройки",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        TryRefresh(() => RefreshServices());
        TryRefresh(RefreshTemplates);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        var instructions = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Выберите каталог app-params и папку сервиса с J2 файлами. " +
                   "Имя сервиса предлагается по J2-папке; папка сервиса в app-params необязательна. " +
                   "Для сохранения или прямого заполнения скопируйте полную страницу Confluence."
        };
        root.Controls.Add(instructions, 0, 0);
        _clipboardIndicator.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(_clipboardIndicator, 0, 1);

        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 6 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        for (var row = 0; row < fields.RowCount; row++)
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / fields.RowCount));

        AddRow(fields, 0, "Каталог app-params", _appParamsRoot,
            MakeButton("Выбрать…", (_, _) => BrowseFolder(_appParamsRoot, "Каталог app-params",
                () => RefreshServices(preferFolder: true))));
        AddRow(fields, 1, "Папка сервиса с J2", _serviceFolder,
            MakeButton("Выбрать…", (_, _) => BrowseFolder(_serviceFolder, "Папка сервиса с J2", RefreshForServiceFolder)));
        AddRow(fields, 2, "Имя сервиса / YAML-папка", _service,
            MakeButton("Обновить", (_, _) => TryRefresh(() => RefreshServices())));
        AddRow(fields, 3, "Основной J2 файл", _template,
            MakeButton("Обновить", (_, _) => TryRefresh(RefreshTemplates)));
        AddRow(fields, 4, "Название сервиса", _displayName);
        AddRow(fields, 5, "Команда", _team);
        root.Controls.Add(fields, 0, 2);

        var descriptions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        descriptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        descriptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        descriptions.Controls.Add(new Label
        {
            Text = "Описания переменных для таблиц параметров и микросервисов",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _editKeyDescriptions.Margin = new Padding(4, 3, 0, 3);
        descriptions.Controls.Add(_editKeyDescriptions, 1, 0);
        root.Controls.Add(descriptions, 0, 3);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.Controls.Add(_saveTemplate, 0, 0);
        actions.Controls.Add(_generateCurrent, 1, 0);
        actions.Controls.Add(_generateFromDefault, 2, 0);
        root.Controls.Add(actions, 0, 4);
        _status.AutoEllipsis = true;
        root.Controls.Add(_status, 0, 5);
        Controls.Add(root);
        AcceptButton = _generateCurrent;
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill };
        button.Click += onClick;
        return button;
    }

    private static void AddRow(TableLayoutPanel table, int row, string label, Control input, Control? action = null)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        input.Margin = new Padding(4, 7, 4, 7);
        table.Controls.Add(input, 1, row);
        if (action is not null)
        {
            action.Margin = new Padding(4, 5, 0, 5);
            table.Controls.Add(action, 2, row);
        }
    }

    private void BrowseFolder(TextBox target, string description, Action refresh)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        target.Text = dialog.SelectedPath;
        TryRefresh(refresh);
    }

    private void TryRefresh(Action refresh)
    {
        try { refresh(); }
        catch (Exception error) { _status.Text = error.Message; }
    }

    private string CurrentService => _service.Text.Trim();

    private void RefreshForServiceFolder()
    {
        RefreshTemplates();
        RefreshServices(preferFolder: true);
    }

    private void RefreshServices(bool preferFolder = false)
    {
        CaptureIdentity();
        var current = CurrentService;
        var names = ServiceInputs.FindServices(_appParamsRoot.Text.Trim());
        var suggested = ServiceInputs.SuggestServiceName(_serviceFolder.Text.Trim(), names);
        var preferred = preferFolder ? suggested : current.Length > 0 ? current : _settings.SelectedService;
        if (string.IsNullOrWhiteSpace(preferred)) preferred = suggested;
        _refreshing = true;
        try
        {
            _service.Items.Clear();
            _service.Items.AddRange(names.Cast<object>().ToArray());
            var index = names.ToList().FindIndex(name =>
                name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _service.SelectedIndex = index;
            else _service.Text = preferred;
        }
        finally
        {
            _refreshing = false;
        }

        LoadIdentity();
    }

    private void RefreshTemplates()
    {
        var preferred = _template.SelectedItem as string ?? _settings.SelectedTemplate;
        var names = ServiceInputs.FindTemplates(_serviceFolder.Text.Trim());
        _template.Items.Clear();
        _template.Items.AddRange(names.Cast<object>().ToArray());
        var index = names.ToList().FindIndex(name =>
            name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = names.ToList().FindIndex(name =>
                name.Equals("appsettings.deploy.j2", StringComparison.OrdinalIgnoreCase));
        if (index < 0 && names.Count == 1) index = 0;
        if (index >= 0) _template.SelectedIndex = index;
    }

    private void CaptureIdentity()
    {
        if (_activeService is null) return;
        _settings.Services[_activeService] = new ServiceIdentity
        {
            DisplayName = _displayName.Text.Trim(),
            Team = _team.Text.Trim()
        };
    }

    private void LoadIdentity()
    {
        _activeService = CurrentService.Length > 0 ? CurrentService : null;
        var identity = _activeService is not null &&
                       _settings.Services.TryGetValue(_activeService, out var stored)
            ? stored : null;
        _displayName.Text = identity?.DisplayName ?? "";
        _team.Text = identity?.Team ?? "";
    }

    private void SaveSettings()
    {
        CaptureIdentity();
        _settings.AppParamsRoot = _appParamsRoot.Text.Trim();
        _settings.ServiceFolder = _serviceFolder.Text.Trim();
        _settings.SelectedService = CurrentService;
        _settings.SelectedTemplate = _template.SelectedItem as string ?? "";
        _settings.Save();
    }

    private void EditKeyDescriptions(object? sender, EventArgs e)
    {
        using var dialog = new KeyDescriptionsForm(_settings.KeyDescriptions);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var previousDescriptions = _settings.KeyDescriptions;
        _settings.KeyDescriptions = dialog.Descriptions;
        try
        {
            SaveSettings();
            _status.Text = "Описания ключей сохранены в локальных настройках.";
        }
        catch (Exception error)
        {
            _settings.KeyDescriptions = previousDescriptions;
            _status.Text = "Не удалось сохранить описания ключей.";
            MessageBox.Show(this, error.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshClipboardIndicator()
    {
        try
        {
            var html = WindowsClipboard.ReadHtmlOnce();
            if (html == _generatedClipboardHtml)
            {
                SetClipboardIndicator("В буфере готовый паспорт. Для нового запуска скопируйте исходный шаблон.",
                    Color.DarkGoldenrod);
                return;
            }

            if (html == _lastCheckedClipboardHtml) return;
            _lastCheckedClipboardHtml = html;
            if (ClipboardTemplateProbe.LooksLikeTemplate(html))
                SetClipboardIndicator("Шаблон распознан по характерным полям. Для прямого заполнения нужна вся страница.",
                    Color.ForestGreen);
            else
                SetClipboardIndicator("В буфере HTML, но признаки шаблона не найдены.", Color.Firebrick);
        }
        catch (InvalidDataException)
        {
            _lastCheckedClipboardHtml = null;
            SetClipboardIndicator("В буфере нет подходящего HTML. Скопируйте шаблон из Confluence.", Color.Firebrick);
        }
        catch (ExternalException)
        {
            _lastCheckedClipboardHtml = null;
            SetClipboardIndicator("Буфер обмена временно занят. Проверка повторится автоматически.", Color.DarkGoldenrod);
        }
        catch (Exception error)
        {
            _lastCheckedClipboardHtml = null;
            SetClipboardIndicator($"Не удалось проверить буфер: {error.Message}", Color.Firebrick);
        }
    }

    private void SetClipboardIndicator(string message, Color color)
    {
        _clipboardIndicator.Text = message;
        _clipboardIndicator.ForeColor = color;
    }

    private void SaveDefaultTemplate(object? sender, EventArgs e)
    {
        var previousTemplate = _settings.DefaultTemplateHtml;
        try
        {
            var html = WindowsClipboard.ReadHtml();
            if (html == _generatedClipboardHtml)
                throw new InvalidDataException("В буфере готовый паспорт. Скопируйте исходную страницу шаблона.");
            TemplateValueMerger.ValidateFullTemplate(html);
            _settings.DefaultTemplateHtml = html;
            SaveSettings();
            _generateFromDefault.Enabled = true;
            _status.Text = "Шаблон по умолчанию сохранён в локальных настройках.";
        }
        catch (Exception error)
        {
            _settings.DefaultTemplateHtml = previousTemplate;
            _status.Text = "Не удалось сохранить шаблон.";
            MessageBox.Show(this, error.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void GeneratePassport(bool useDefaultTemplate)
    {
        _generateCurrent.Enabled = false;
        _generateFromDefault.Enabled = false;
        _saveTemplate.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var appParamsRoot = ExistingDirectory(_appParamsRoot.Text, "каталог app-params");
            var serviceFolder = ExistingDirectory(_serviceFolder.Text, "папку сервиса с J2");
            var service = CurrentService;
            if (service.Length == 0)
                service = ServiceInputs.SuggestServiceName(serviceFolder, ServiceInputs.FindServices(appParamsRoot));
            if (service.Length == 0)
                throw new InvalidDataException("Укажите имя сервиса или выберите папку сервиса с J2.");
            var template = _template.SelectedItem as string
                ?? throw new InvalidDataException("Выберите основной J2 файл.");
            var j2File = Path.Combine(serviceFolder, template);
            if (!File.Exists(j2File))
                throw new FileNotFoundException("Основной J2 файл не найден.", j2File);
            if (string.IsNullOrWhiteSpace(_displayName.Text) || string.IsNullOrWhiteSpace(_team.Text))
                throw new InvalidDataException("Укажите название сервиса и команду.");

            var clipboardHtml = WindowsClipboard.ReadHtml();
            if (!useDefaultTemplate && clipboardHtml == _generatedClipboardHtml)
                throw new InvalidDataException("В буфере готовый паспорт. Скопируйте исходную страницу шаблона.");
            if (useDefaultTemplate && string.IsNullOrWhiteSpace(_settings.DefaultTemplateHtml))
                throw new InvalidDataException("Сначала сохраните полную страницу как шаблон по умолчанию.");
            var source = useDefaultTemplate
                ? TemplateValueMerger.Merge(_settings.DefaultTemplateHtml, clipboardHtml)
                : clipboardHtml;
            var options = new PassportOptions(service, appParamsRoot, j2File,
                _displayName.Text.Trim(), _team.Text.Trim(), _settings.KeyDescriptions);
            var result = PassportGenerator.Generate(source, options);
            var generatedHtml = CfHtml.Build(result.HtmlFragment);
            WindowsClipboard.Write(generatedHtml, result.PlainText);
            if (CurrentService.Length == 0)
            {
                _refreshing = true;
                try { _service.Text = service; }
                finally { _refreshing = false; }
                _activeService = service;
            }
            _generatedClipboardHtml = generatedHtml;
            _lastCheckedClipboardHtml = generatedHtml;
            SetClipboardIndicator("В буфере готовый паспорт. Для нового запуска скопируйте исходный шаблон.",
                Color.DarkGoldenrod);
            _status.Text = "Паспорт скопирован. Вставьте его в редактор Confluence.";
            try { SaveSettings(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Паспорт скопирован, настройки не сохранены",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception error)
        {
            _status.Text = "Не удалось сформировать паспорт.";
            MessageBox.Show(this, error.Message, "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            _generateCurrent.Enabled = true;
            _generateFromDefault.Enabled = !string.IsNullOrWhiteSpace(_settings.DefaultTemplateHtml);
            _saveTemplate.Enabled = true;
        }
    }

    private static string ExistingDirectory(string input, string label)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidDataException($"Укажите {label}.");
        var path = Path.GetFullPath(input.Trim());
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Не найдена {label}: {path}");
        return path;
    }
}
