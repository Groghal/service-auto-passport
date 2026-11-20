using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class KafkaTopicReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kafka-topic-report-" + Guid.NewGuid().ToString("N"));

    private string Write(string path, string content)
    {
        var file = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    private static KafkaTopicResult Render(string json, IReadOnlyList<KafkaTopicRule>? rules = null)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return KafkaTopicReport.Render(input, rules ?? KafkaTopicReport.DefaultRules);
    }

    [Fact]
    public void BuildsOneConfluenceTablePerClusterAndMergesDirectionsWithinIt()
    {
        var result = Render("""
            {
              "DirectKafka": {"Consumers": ["ignored"], "Consumer": {"Topics": ["Shared"]}},
              "ArbitraryRoot": {
                "Production": {
                  "BootstrapServers": "hidden-broker.example",
                  "Consumer": {"Topics": ["Shared", "alpha", "Shared", null, 42, " "]},
                  "Producers": [{"TopicId": "Shared"}, {"TopicId": "OnlyWrite"}, {"TopicId": "shared"}]
                },
                "Archive": {"Producers": [{"TopicId": "Shared"}], "Consumer": {"OtherTopics": ["ignored"]}}
              },
              "Unrelated": {"Topics": ["ignored"], "TopicId": "ignored"}
            }
            """);
        Assert.Equal(3, result.ClusterCount);
        Assert.Equal(6, result.TopicCount);

        var html = new HtmlDocument();
        html.LoadHtml(CfHtml.ExtractFragment(CfHtml.Build(result.HtmlFragment)));
        Assert.Equal(new[] { "ArbitraryRoot.Archive", "ArbitraryRoot.Production", "DirectKafka" },
            html.DocumentNode.Elements("h3").Select(heading => heading.InnerText));
        var tables = html.DocumentNode.Elements("table").ToArray();
        Assert.Equal(3, tables.Length);
        Assert.All(tables, table =>
        {
            Assert.True(table.HasClass("confluenceTable"));
            Assert.Equal(new[] { "Топик", "Чтение", "Запись" }, table.Descendants("th").Select(cell => cell.InnerText));
        });
        static IEnumerable<string> Rows(HtmlNode table) => table.Descendants("tr").Skip(1)
            .Select(row => string.Join("|", row.Elements("td").Select(cell => cell.InnerText)));
        Assert.Equal(new[] { "Shared|—|Да" }, Rows(tables[0]));
        Assert.Equal(new[] { "OnlyWrite|—|Да", "Shared|Да|Да", "alpha|Да|—", "shared|—|Да" }, Rows(tables[1]));
        Assert.Equal(new[] { "Shared|Да|—" }, Rows(tables[2]));
        Assert.Contains("Shared\tДа\tДа", result.PlainText);
        Assert.DoesNotContain("ignored", result.HtmlFragment);
        Assert.DoesNotContain("hidden-broker.example", result.HtmlFragment + result.PlainText);
    }

    [Fact]
    public void ReloadsCustomRulesAndEscapesNamesInTheClipboardHtml()
    {
        var rules = new List<KafkaTopicRule>
        {
            new(KafkaTopicDirection.Read, "Profiles[].{kafka}.Subscriber.Source.TopicName"),
            new(KafkaTopicDirection.Write, "Profiles[].{kafka}.Publisher.*.TopicName")
        };
        var settingsJson = JsonSerializer.Serialize(rules, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restored = JsonSerializer.Deserialize<List<KafkaTopicRule>>(settingsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(rules, restored);
        KafkaTopicReport.ValidateRules(restored);
        var result = Render("""
            {
              "profiles": [
                {"Kafka<&>": {"subscriber": {"source": {"topicname": "Event<&>"}}, "Publisher": {"route": {"TopicName": "Event<&>"}}}},
                {"Kafka<&>": {"Publisher": {"route": {"TopicName": "Other"}}}}
              ],
              "Wrapper": {"Profiles": [{"Unexpected": {"Subscriber": {"Source": {"TopicName": "ignored"}}}}]}
            }
            """, restored);
        Assert.Equal(2, result.ClusterCount);
        Assert.Equal(2, result.TopicCount);
        Assert.Contains("profiles[0].Kafka&lt;&amp;&gt;", result.HtmlFragment);
        Assert.Contains("profiles[1].Kafka&lt;&amp;&gt;", result.HtmlFragment);
        Assert.Contains("Event&lt;&amp;&gt;", result.HtmlFragment);
        Assert.DoesNotContain("ignored", result.HtmlFragment);
        Assert.Contains("Event<&>\tДа\tДа", result.PlainText);
    }

    [Fact]
    public void ReadsProdTopicsFromAllServiceTemplatesWithNetworkLayering()
    {
        Write("params/application.yml", "TOPIC: base.topic\nREAD_TOPIC: '{{ TOPIC }}'\n");
        Write("params/application-prod.yml", "TOPIC: global.prod.topic\n");
        Write("params/application-test.yml", "TOPIC: never.test.topic\n");
        Write("params/a/a.yml", "TOPIC: service.base.topic\n");
        Write("params/a/a-prod.yaml", "TOPIC: a.prod.topic\nSECRET_TOPIC: a.prod.topic\nTOPIC_LIST: [from.list, '{{ READ_TOPIC }}']\n");
        var a = Write("A/appsettings.deploy.j2", """
            {
              "KafkaSetting": {
                "Cdc": {
                  "Consumer": {"Topics": ["{{ READ_TOPIC }}"]},
                  "Producers": [{"TopicId": "old.secret.topic"}],
                  "Password": "{{ UNAVAILABLE_PASSWORD }}"
                },
                "Events": {"Consumer": {"Topics": {{ TOPIC_LIST }} }}
              }
            }
            """);
        Write("A/appsettings.secrets.j2", "KafkaSetting__Cdc__Producers__0__TopicId: '{{ SECRET_TOPIC }}'\n");
        var b = Write("B/appsettings.deploy.j2", """
            {"KafkaSetting": {"Cdc": {"Producers": [{"TopicId": "{{ TOPIC }}"}, {"TopicId": "a.prod.topic"}]}}}
            """);
        var withoutKafka = Write("C/appsettings.deploy.j2", """{"Logging": {"Password": "{{ UNAVAILABLE_PASSWORD }}"}}""");
        var services = new NetworkServiceInput[]
        {
            new() { J2File = a, YamlService = "a", Label = "Reader" },
            new() { J2File = b, YamlService = "missing-folder", Label = "Writer" },
            new() { J2File = withoutKafka, Label = "No Kafka" }
        };

        var result = KafkaTopicReport.RenderForServices(Path.Combine(_root, "params"), services, KafkaTopicReport.DefaultRules);

        Assert.Equal(3, result.ClusterCount);
        Assert.Equal(5, result.TopicCount);
        var document = new HtmlDocument();
        document.LoadHtml(CfHtml.ExtractFragment(CfHtml.Build(result.HtmlFragment)));
        Assert.Equal(new[] { "Reader — KafkaSetting.Cdc", "Reader — KafkaSetting.Events", "Writer — KafkaSetting.Cdc" },
            document.DocumentNode.Elements("h3").Select(heading => heading.InnerText));
        Assert.Contains("a.prod.topic\tДа\tДа", result.PlainText);
        Assert.Contains("from.list\tДа\t—", result.PlainText);
        Assert.Contains("a.prod.topic\t—\tДа", result.PlainText);
        Assert.Contains("global.prod.topic\t—\tДа", result.PlainText);
        Assert.DoesNotContain("old.secret.topic", result.HtmlFragment);
        Assert.DoesNotContain("never.test.topic", result.HtmlFragment);
        Assert.DoesNotContain("UNAVAILABLE_PASSWORD", result.HtmlFragment);
    }

    [Fact]
    public void CopiesEachClustersProdBrokerVariableAndAddressesAboveItsTopics()
    {
        Write("params/application.yml", "CDC_HOST: base.example:9092\nDIRECT_HOST: direct.example\n");
        Write("params/application-prod.yml", "CDC_HOST: global.prod.example:9093\n");
        Write("params/a/a-prod.yml", "CDC_HOST: 'a.prod.example:9192, a.replica.example:9193'\nBROKER_LIST: [array-one.example:9092, array-two.example:9093]\n");
        var a = Write("A/appsettings.deploy.j2", """
            {
              "KafkaSetting": {
                "Cdc": {"BootstrapServers": "{{ CDC_HOST }}", "Consumer": {"Topics": ["cdc.read"]}},
                "Events": {"BootstrapServers": ["[2001:db8::1]:9092", "kafka://client:secret@secure.example:9094/path?token=hidden", "{{ DIRECT_HOST }}"],
                           "Producers": [{"TopicId": "events.write"}]},
                "Archive": {"BootstrapServers": "{{ BROKER_LIST }}", "Consumer": {"Topics": ["archives"]}},
                "Local": {"BootstrapServers": "literal.example:9095", "Consumer": {"Topics": ["local"]}}
              }
            }
            """);
        var b = Write("B/appsettings.deploy.j2", """
            {
              "KafkaSetting": {
                "Cdc": {"bootstrapServers": "{{ CDC_HOST }}", "Consumer": {"Topics": ["cdc.read"]}},
                "MissingValue": {"BootstrapServers": "{{ UNSET_KAFKA_HOST }}", "Consumer": {"Topics": ["still.copied"]}},
                "MissingField": {"Consumer": {"Topics": ["no.address"]}}
              }
            }
            """);

        var result = KafkaTopicReport.RenderForServices(Path.Combine(_root, "params"),
            [new() { J2File = a, YamlService = "a", Label = "A" }, new() { J2File = b, Label = "B" }], KafkaTopicReport.DefaultRules);

        Assert.Equal(7, result.ClusterCount);
        Assert.Equal(7, result.TopicCount);
        var html = new HtmlDocument();
        html.LoadHtml(CfHtml.ExtractFragment(CfHtml.Build(result.HtmlFragment)));
        var lines = html.DocumentNode.Elements("h3").ToDictionary(heading => heading.InnerText, heading =>
        {
            var addresses = Assert.IsType<HtmlNode>(heading.NextSibling);
            Assert.Equal("p", addresses.Name);
            Assert.Equal("table", addresses.NextSibling!.Name);
            return addresses.InnerText;
        });
        Assert.Equal("CDC_HOST → a.prod.example:9192, a.replica.example:9193", lines["A — KafkaSetting.Cdc"]);
        Assert.Equal("DIRECT_HOST → [2001:db8::1]:9092, secure.example:9094, direct.example", lines["A — KafkaSetting.Events"]);
        Assert.Equal("BROKER_LIST → array-one.example:9092, array-two.example:9093", lines["A — KafkaSetting.Archive"]);
        Assert.Equal("Брокеры prod → literal.example:9095", lines["A — KafkaSetting.Local"]);
        Assert.Equal("CDC_HOST → global.prod.example:9093", lines["B — KafkaSetting.Cdc"]);
        Assert.Equal("UNSET_KAFKA_HOST → Адрес prod не найден", lines["B — KafkaSetting.MissingValue"]);
        Assert.Equal("Брокеры prod → Адрес prod не найден", lines["B — KafkaSetting.MissingField"]);
        Assert.Contains("still.copied", result.PlainText);
        Assert.DoesNotContain("secret", result.HtmlFragment + result.PlainText);
        Assert.DoesNotContain("token", result.HtmlFragment + result.PlainText);
        Assert.DoesNotContain("base.example", result.HtmlFragment + result.PlainText);
        foreach (var line in lines.Values) Assert.Contains(line, result.PlainText);
    }

    [Fact]
    public void ReportsTheServiceAndTopicPathWhenAProdVariableCannotBeResolved()
    {
        Write("params/application.yml", "");
        var valid = Write("Ready/appsettings.deploy.j2", """{"Kafka":{"Consumer":{"Topics":["ready"]}}}""");
        var missing = Write("Missing/appsettings.deploy.j2", """
            {"Kafka":{"Consumer":{"Topics":["{{ MISSING_TOPIC }}"]}}}
            """);
        var error = Assert.Throws<InvalidDataException>(() => KafkaTopicReport.RenderForServices(Path.Combine(_root, "params"),
            [new() { J2File = valid, Label = "Ready" }, new() { J2File = missing, Label = "Missing" }], KafkaTopicReport.DefaultRules));
        Assert.Contains("Missing", error.Message);
        Assert.Contains("Kafka.Consumer.Topics[0]", error.Message);
        Assert.Contains("MISSING_TOPIC", error.Message);
    }

    [Fact]
    public void ReadsUtf8BomCommentsAndTrailingCommasFromAppsettings()
    {
        var json = """
            {
              // This file uses the appsettings JSON dialect.
              "Kafka": { "Consumer": { "Topics": ["one",], }, },
            }
            """;
        using var stream = new MemoryStream([.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(json)]);
        var result = KafkaTopicReport.Render(stream, KafkaTopicReport.DefaultRules);
        Assert.Equal(1, result.ClusterCount);
        Assert.Equal(1, result.TopicCount);
    }

    [Theory]
    [InlineData("{\"Kafka\": ")]
    [InlineData("[1, 2]")]
    public void RejectsMalformedJsonAndNonObjectRoots(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() => Render(json));
        Assert.Contains("JSON", error.Message);
    }

    [Theory]
    [InlineData("*.Consumer.Topics[]")]
    [InlineData("{kafka}.{kafka}.Topics[]")]
    [InlineData("{kafka}.Producer..TopicId")]
    [InlineData("{kafka}.Topics[][]")]
    [InlineData("*.{kafka}")]
    public void RejectsInvalidPathsWithTheRuleNumber(string path)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            KafkaTopicReport.ValidateRules([KafkaTopicReport.DefaultRules[0], new(KafkaTopicDirection.Read, path)]));
        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void EmptyOrUnmatchedRulesYieldNoClipboardContent()
    {
        Assert.Equal(new KafkaTopicResult("", "", 0, 0), Render("""{"Kafka":{"Consumer":{"Topics":"scalar"}}}"""));
        Assert.Equal(new KafkaTopicResult("", "", 0, 0), Render("{}", []));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
