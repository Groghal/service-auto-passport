using System.ComponentModel;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport;

internal sealed class NetworkMapControl : UserControl
{
    private readonly LocalSettings _settings;
    private readonly TextBox _appParamsRoot = new() { Dock = DockStyle.Fill };
    private readonly Label _clipboardIndicator = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button _analyze = new() { Text = "Анализ: базовая таблица", AutoSize = true };
    private readonly Button _analyzeSaved = new() { Text = "Анализ: сохранённая таблица", AutoSize = true };
    private string? _template;
    private string? _prefillClipboardTemplate;
    private string Root => _appParamsRoot.Text.Trim();
    public Button AnalyzeButton => _analyze;
    private readonly BindingList<NetworkServiceInput> _services;
    private readonly DataGridView _inputs = Grid();
    private readonly DataGridView _review = Grid();
    private readonly TextBox _environments = new() { Dock = DockStyle.Fill };
    private readonly TextBox _ports = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _diagnostics = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button _copyTable = new() { Text = "Копировать таблицу", AutoSize = true, Enabled = false };
    private readonly Button _copyXml = new() { Text = "Копировать draw.io XML", AutoSize = true, Enabled = false };
    private NetworkAnalysis? _analysis;
    private bool _binding;
    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
    };
    public NetworkMapControl(LocalSettings settings)
    {
        _settings = settings;
        Dock = DockStyle.Fill;
        _appParamsRoot.Text = settings.NetworkAppParamsRoot ?? settings.AppParamsRoot;
        _services = new BindingList<NetworkServiceInput>(settings.Network.Services);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new Padding(10) };
        foreach (var height in new[] { 45F, 35F, 35F, 35F, 170F, 30F, 90F, 32F, 75F, 0F, 80F })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Добавьте main J2 каждого сервиса. Secrets подключаются автоматически. Адресные переменные (через запятую) связывают вызовы с узлами выбранных сервисов." }, 0, 0);
        layout.Controls.Add(_clipboardIndicator, 0, 1);
        var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rootRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        rootRow.Controls.Add(new Label { Text = "Каталог app-params", Dock = DockStyle.Fill });
        rootRow.Controls.Add(_appParamsRoot);
        var browse = new Button { Text = "Выбрать…", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { SelectedPath = Directory.Exists(Root) ? Root : "", ShowNewFolderButton = false };
            if (picker.ShowDialog(this) == DialogResult.OK) _appParamsRoot.Text = picker.SelectedPath;
        };
        rootRow.Controls.Add(browse);
        layout.Controls.Add(rootRow, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var add = new Button { Text = "Добавить J2…", AutoSize = true };
        add.Click += (_, _) => Run(AddFiles);
        var remove = new Button { Text = "Удалить сервис", AutoSize = true };
        remove.Click += (_, _) => { if (_inputs.CurrentRow?.DataBoundItem is NetworkServiceInput s) _services.Remove(s); InvalidateAnalysis(); };
        var saveTemplate = new Button { Text = "Сохранить шаблон таблицы из буфера", AutoSize = true };
        saveTemplate.Click += (_, _) => Run(() =>
        {
            _settings.Network.TemplateHtml = NetworkMapGenerator.ExtractTableTemplate(WindowsClipboard.ReadHtml());
            _settings.Save();
            InvalidateAnalysis();
            _analyzeSaved.Enabled = true;
            _diagnostics.Text = "Шаблон таблицы сохранён.";
        });
        var kafkaTopics = new Button { Text = "Топики Kafka", AutoSize = true };
        kafkaTopics.Click += (_, _) => Run(CopyKafkaTopics);
        var kafkaRules = new Button { Text = "Правила топиков…", AutoSize = true };
        kafkaRules.Click += (_, _) => Run(EditKafkaTopicRules);
        actions.Controls.AddRange(new Control[] { add, remove, saveTemplate, kafkaTopics, kafkaRules });
        layout.Controls.Add(actions, 0, 3);
        foreach (var item in new[] { ("J2File", "Main J2"), ("Label", "Название"), ("YamlService", "YAML-папка") })
            _inputs.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = item.Item1, HeaderText = item.Item2 });
        _inputs.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NetworkServiceInput.AddressVariables), HeaderText = "Собственные адресные переменные",
            DefaultCellStyle = new DataGridViewCellStyle { DataSourceNullValue = "" }
        });
        _inputs.Columns.Add(BlockColumn(nameof(NetworkServiceInput.Block)));
        _inputs.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(NetworkServiceInput.ParentBlock), HeaderText = "Блок-родитель",
            ToolTipText = "Внешний контейнер для указанного блока. Если «Блок» пуст, сервис помещается прямо в блок-родитель.",
            DefaultCellStyle = new DataGridViewCellStyle { DataSourceNullValue = "" }
        });
        _inputs.DataSource = _services; layout.Controls.Add(_inputs, 0, 4);
        var envRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        envRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); envRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        envRow.Controls.Add(new Label { Text = "Окружения по порядку", Dock = DockStyle.Fill }); envRow.Controls.Add(_environments); layout.Controls.Add(envRow, 0, 5);
        _environments.Text = string.Join(", ", settings.Network.Environments);
        var portRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        portRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); portRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        portRow.Controls.Add(new Label { Text = "Порты по умолчанию\nПротокол=порт, по строке", Dock = DockStyle.Fill }); portRow.Controls.Add(_ports); layout.Controls.Add(portRow, 0, 6);
        _ports.Text = string.Join(Environment.NewLine, settings.Network.Ports.Select(p => p.Key + "=" + p.Value));
        var displayOptions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var showProtocols = new CheckBox { Text = "Показать протокол рядом с портом", AutoSize = true, Checked = !settings.Network.Display.HideProtocolsOnArrows };
        var showKeys = new CheckBox { Text = "Показать название ключа", AutoSize = true, Checked = settings.Network.Display.ShowConfigurationKeys };
        var colorArrows = new CheckBox { Text = "Цвет стрелок по сервисам", AutoSize = true, Checked = settings.Network.Display.ColorArrowsByService };
        showProtocols.CheckedChanged += (_, _) => settings.Network.Display.HideProtocolsOnArrows = !showProtocols.Checked;
        showKeys.CheckedChanged += (_, _) => settings.Network.Display.ShowConfigurationKeys = showKeys.Checked;
        colorArrows.CheckedChanged += (_, _) => settings.Network.Display.ColorArrowsByService = colorArrows.Checked;
        displayOptions.Controls.AddRange(new Control[] { showProtocols, showKeys, colorArrows });
        layout.Controls.Add(displayOptions, 0, 7);
        var generate = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        _analyze.Click += (_, _) => Run(() => Analyze(TemplateSource.Basic));
        var analyzeClipboard = new Button { Text = "Анализ: таблица из буфера", AutoSize = true };
        analyzeClipboard.Click += (_, _) => Run(() => Analyze(TemplateSource.Clipboard));
        _analyzeSaved.Click += (_, _) => Run(() => Analyze(TemplateSource.Saved));
        _analyzeSaved.Enabled = !string.IsNullOrWhiteSpace(settings.Network.TemplateHtml);
        var parameters = new Button { Text = "Параметры сети…", AutoSize = true };
        parameters.Click += (_, _) => EditNetworkParameters();
        generate.Controls.AddRange(new Control[] { _analyze, analyzeClipboard, _analyzeSaved, parameters, _copyTable, _copyXml }); layout.Controls.Add(generate, 0, 8);
        layout.Controls.Add(_review, 0, 9); layout.Controls.Add(_diagnostics, 0, 10); Controls.Add(layout);
        _appParamsRoot.TextChanged += (_, _) => InvalidateAnalysis();
        _inputs.CellValueChanged += (_, _) => InvalidateAnalysis();
        _environments.TextChanged += (_, _) => InvalidateAnalysis(); _ports.TextChanged += (_, _) => InvalidateAnalysis();
        _review.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_review.IsCurrentCellDirty && _review.CurrentCell is DataGridViewCheckBoxCell)
            {
                _review.CommitEdit(DataGridViewDataErrorContexts.Commit);
                _review.EndEdit();
            }
        };
        _review.CellValueChanged += (_, _) =>
        {
            if (!_binding && _analysis is not null) Run(() =>
            {
                NetworkMapGenerator.Refresh(_analysis, settings.Network.Ports);
                _settings.Save();
                _review.Refresh();
            });
        };
        _review.CellFormatting += FormatEndpoint;
        _copyTable.Click += (_, _) => Run(() =>
        {
            var result = NetworkMapGenerator.RenderTable(_template!, ReviewedAnalysis());
            WindowsClipboard.Write(CfHtml.Build(result.HtmlFragment), result.PlainText);
            _diagnostics.Text = "Макрос Table Transformer скопирован. Вставьте его в редактор Confluence.";
        });
        _copyXml.Click += (_, _) => Run(() => { var xml = NetworkMapGenerator.CreateDiagram(ReviewedAnalysis()); Clipboard.SetDataObject(xml, true, 10, 50); _diagnostics.Text = "XML скопирован. В draw.io откройте Extras → Edit Diagram, замените XML и нажмите Apply."; });
        _diagnostics.Text = string.IsNullOrWhiteSpace(settings.Network.TemplateHtml)
            ? "Добавьте сервисы и нажмите «Анализ: базовая таблица»."
            : "Шаблон таблицы сохранён. Добавьте сервисы и выполните анализ.";
        RefreshClipboardIndicator();
    }
    public void RefreshClipboardIndicator()
    {
        try
        {
            NetworkMapGenerator.ExtractTableTemplate(WindowsClipboard.ReadHtmlOnce());
            _clipboardIndicator.Text = "В буфере Table Transformer с исходной таблицей.";
            _clipboardIndicator.ForeColor = Color.ForestGreen;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _clipboardIndicator.Text = "Буфер обмена временно занят.";
            _clipboardIndicator.ForeColor = Color.DarkGoldenrod;
        }
        catch (Exception)
        {
            _clipboardIndicator.Text = "Скопируйте Table Transformer или выберите базовую таблицу.";
            _clipboardIndicator.ForeColor = Color.DimGray;
        }
    }

    private void Run(Action action)
    {
        try { UseWaitCursor = true; action(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Сетевая карта", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { UseWaitCursor = false; }
    }
    private void InvalidateAnalysis() { _analysis = null; _copyTable.Enabled = _copyXml.Enabled = false; _review.DataSource = null; }
    public void SaveInputs()
    {
        _inputs.EndEdit();
        _settings.NetworkAppParamsRoot = Root;
        _settings.Network.Services = _services.ToList();
        _settings.Network.Environments = NetworkMapGenerator.Split(_environments.Text).ToList();
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _ports.Lines.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535 || !ports.TryAdd(parts[0], port))
                throw new InvalidDataException("Некорректная строка портов. Используйте Протокол=порт, без повторов.");
        }
        _settings.Network.Ports = ports;
    }
    private void AddFiles()
    {
        if (!Directory.Exists(Root)) throw new DirectoryNotFoundException("Выберите каталог app-params на вкладке сетевых подключений.");
        using var picker = new OpenFileDialog { Multiselect = true, Filter = "J2 templates|*.j2" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        foreach (var file in picker.FileNames)
        {
            if (Path.GetFileName(file).EndsWith("secrets.j2", StringComparison.OrdinalIgnoreCase)) continue;
            if (_services.Any(s => s.J2File.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            var folder = Path.GetDirectoryName(file)!;
            var deploymentName = ServiceComponent.DeploymentName(folder);
            var yaml = ServiceInputs.SuggestServiceName(folder, ServiceInputs.FindServices(Root));
            if (!_settings.Services.TryGetValue(deploymentName, out var identity)) _settings.Services.TryGetValue(yaml, out identity);
            _services.Add(new NetworkServiceInput { J2File = file, Label = deploymentName, YamlService = yaml, AddressVariables = identity?.DomainApiVariable ?? "" });
        }
        InvalidateAnalysis();
    }
    private void CopyKafkaTopics()
    {
        if (_settings.KafkaTopicRules.Count == 0)
        {
            _diagnostics.Text = "Добавьте путь к топикам в окне «Правила топиков…».";
            return;
        }
        if (!_inputs.EndEdit()) return;
        var result = KafkaTopicReport.RenderForServices(Root, _services.ToArray(), _settings.KafkaTopicRules);
        if (result.TopicCount == 0)
        {
            _diagnostics.Text = "В prod-конфигурациях добавленных сервисов топики по заданным правилам не найдены.";
            return;
        }
        WindowsClipboard.Write(CfHtml.Build(result.HtmlFragment), result.PlainText);
        _diagnostics.Text = $"Скопировано строк с топиками — {result.TopicCount}, таблиц по сервисам и Kafka — {result.ClusterCount}. Вставьте их в редактор Confluence.";
    }

    private void EditKafkaTopicRules()
    {
        using var dialog = new KafkaTopicRulesForm(_settings.KafkaTopicRules);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var previous = _settings.KafkaTopicRules;
        _settings.KafkaTopicRules = dialog.Rules;
        try { _settings.Save(); }
        catch
        {
            _settings.KafkaTopicRules = previous;
            throw;
        }
        _diagnostics.Text = "Правила топиков Kafka сохранены.";
    }

    private enum TemplateSource { Basic, Clipboard, Saved }

    private void EditNetworkParameters()
    {
        var analysis = _analysis;
        using var dialog = new NetworkParametersForm(_settings.Network.Targets, analysis?.Connections ?? [], _services.ToArray(),
            entries => NetworkParameterCatalog.SaveCatalog(_settings.Network, entries, analysis, _settings.Save),
            draft => NetworkTemplatePrefill.ReadFirstWithRows(PrefillSources(), analysis?.Connections ?? [], draft));
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _diagnostics.Text = "Параметры сети сохранены. Правки будут видны при копировании таблицы и схемы.";
    }

    private IEnumerable<(string Name, string? Html)> PrefillSources()
    {
        yield return ("Текущий шаблон", _template);
        yield return ("Сохранённый шаблон", _settings.Network.TemplateHtml);
        yield return ("Таблица, скопированная при анализе", _prefillClipboardTemplate);

        string? currentClipboard;
        try { currentClipboard = WindowsClipboard.ReadHtml(); }
        catch (InvalidDataException) { currentClipboard = null; }
        catch (System.Runtime.InteropServices.ExternalException error)
        {
            throw new InvalidDataException("Буфер обмена занят. Повторите предзаполнение.", error);
        }
        yield return ("Буфер обмена", currentClipboard);
    }

    private void Analyze(TemplateSource source)
    {
        InvalidateAnalysis(); SaveInputs();
        _prefillClipboardTemplate = null;
        var template = source switch
        {
            TemplateSource.Clipboard => WindowsClipboard.ReadHtml(),
            TemplateSource.Saved => _settings.Network.TemplateHtml,
            _ => NetworkTableTemplates.BasicHtml
        };
        var tableTemplate = NetworkMapGenerator.ExtractTableTemplate(template);
        if (source == TemplateSource.Saved)
        {
            try { _prefillClipboardTemplate = NetworkMapGenerator.ExtractTableTemplate(WindowsClipboard.ReadHtml()); }
            catch (InvalidDataException) { }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
        _analysis = NetworkMapGenerator.Analyze(Root, _settings.Network);
        _template = tableTemplate;
        _binding = true;
        try
        {
            _review.Columns.Clear();
            _review.AllowUserToDeleteRows = false;
            _review.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = "Include", HeaderText = "Включить", FillWeight = 35,
                ToolTipText = "Включить подключение в схему draw.io и таблицу Confluence" });
            foreach (var item in new[] { ("Service", "Сервис"), ("Path", "Путь"), ("Parameters", "Параметры") })
                _review.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = item.Item1, HeaderText = item.Item2, ReadOnly = true });
            var protocol = new DataGridViewComboBoxColumn { DataPropertyName = "Protocol", HeaderText = "Протокол" };
            protocol.Items.AddRange(_settings.Network.Ports.Keys.Concat(new[] { "Unknown" }).Concat(_analysis.Connections.Select(c => c.Choice.Protocol)).Distinct().Cast<object>().ToArray());
            _review.Columns.Add(protocol);
            var targets = new[] { new Target("", "Внешний / общий узел") }.Concat(_analysis.Services.Select(s => new Target(s.Id, s.Label))).ToList();
            _review.Columns.Add(new DataGridViewComboBoxColumn { DataPropertyName = "Target", HeaderText = "Целевой сервис", DataSource = targets, ValueMember = "Id", DisplayMember = "Label" });
            foreach (var env in _analysis.Environments)
                _review.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = env, ReadOnly = true, Name = env, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            _review.DataSource = new BindingList<ReviewRow>(_analysis.Connections.Select(c => new ReviewRow(c)).ToList());
        }
        finally { _binding = false; }
        _copyTable.Enabled = _copyXml.Enabled = true;
        _diagnostics.Text = string.Join(Environment.NewLine, _analysis.Diagnostics.Append("Проверьте протоколы и значения. Отличия адресов у общих параметров будут показаны с именами сервисов."));
        _settings.Save();
    }
    private void FormatEndpoint(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_analysis is null || e.RowIndex < 0 || e.ColumnIndex < 0 || _review.Columns[e.ColumnIndex].DataPropertyName.Length != 0) return;
        if (_review.Rows[e.RowIndex].DataBoundItem is ReviewRow row &&
            row.Connection.Endpoints.TryGetValue(_review.Columns[e.ColumnIndex].Name, out var value))
            e.Value = value;
    }

    private static DataGridViewTextBoxColumn BlockColumn(string property) => new()
    {
        DataPropertyName = property, HeaderText = "Блок",
        ToolTipText = "Узлы с одинаковым именем блока входят в один контейнер. Для отдельного узла очистите оба поля блока.",
        DefaultCellStyle = new DataGridViewCellStyle { DataSourceNullValue = "" }
    };
    private NetworkAnalysis ReviewedAnalysis()
    {
        _review.EndEdit();
        var analysis = _analysis ?? throw new InvalidDataException("Сначала выполните анализ.");
        NetworkMapGenerator.Refresh(analysis, _settings.Network.Ports);
        _settings.Save();
        return analysis;
    }
    private sealed record Target(string Id, string Label);
    private sealed class ReviewRow(NetworkConnection connection)
    {
        public NetworkConnection Connection => connection;
        public bool Include { get => connection.Choice.Include; set => connection.Choice.Include = value; }
        public string Service => connection.Service.Label;
        public string Path => connection.Path;
        public string Parameters => connection.Parameters;
        public string Protocol { get => connection.Choice.Protocol; set => connection.Choice.Protocol = value; }
        public string Target { get => connection.Choice.TargetService; set { connection.Choice.TargetService = value; connection.Choice.TargetReviewed = true; } }
    }
}
