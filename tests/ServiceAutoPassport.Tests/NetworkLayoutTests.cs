using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public sealed class NetworkLayoutTests
{
    private static NetworkServiceInput[] Services(int count) => Enumerable.Range(0, count).Select(i =>
        new NetworkServiceInput { J2File = Path.Combine(Path.GetTempPath(), "layout", i.ToString(), "appsettings.deploy.j2"), Label = "Service " + i }).ToArray();

    private static NetworkConnection Link(NetworkServiceInput service, string path, string parameter = "SHARED_HOST",
        string name = "Backend", NetworkServiceInput? target = null, string block = "k8s", string port = "") => new()
    {
        Service = service, Path = path, Host = "{{ " + parameter + " }}", Port = port,
        ExternalTarget = new() { Label = name, Block = block },
        Choice = new() { Protocol = "HTTPS", TargetService = target?.Id ?? "" }
    };

    private static NetworkAnalysis Analysis(NetworkServiceInput[] services, params NetworkConnection[] connections) =>
        new(services, Array.Empty<string>(), connections, Array.Empty<string>());

    private static XElement[] Cells(XDocument doc, string attr, string value) =>
        doc.Descendants("mxCell").Where(cell => (string?)cell.Attribute(attr) == value).ToArray();

    private static XElement Cell(XDocument doc, string id) => Assert.Single(Cells(doc, "id", id));
    private static string Attr(XElement cell, string name) => (string)cell.Attribute(name)!;
    private static double Value(XElement element, string name) => double.Parse(Attr(element, name), CultureInfo.InvariantCulture);
    private static Dictionary<string, string> Style(XElement cell) =>
        Attr(cell, "style").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

    private record Box(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
        public bool Overlaps(Box b) => X < b.Right && b.X < Right && Y < b.Bottom && b.Y < Bottom;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1_000_000)]
    public void ServiceColumnsKeepEmptySlotsAndRoutesInBothDirections(int distance)
    {
        var services = Services(3);
        services[0].DiagramColumn = -distance;
        services[2].DiagramColumn = distance;
        var connections = new[]
        {
            Link(services[0], "ToCenter", target: services[1]),
            Link(services[1], "ToRight", target: services[2]),
            Link(services[2], "ToLeft", target: services[0])
        };
        var analysis = Analysis(services, connections);
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var left = Bounds(doc, Cell(doc, services[0].Id));
        var center = Bounds(doc, Cell(doc, services[1].Id));
        var right = Bounds(doc, Cell(doc, services[2].Id));
        Assert.Equal(distance * 444.0, center.X - left.X);
        Assert.Equal(distance * 444.0, right.X - center.X);
        Assert.Equal(left.Y, center.Y);
        Assert.Equal(center.Y, right.Y);
        var frame = Bounds(doc, Assert.Single(Cells(doc, "kind", "container")));
        Assert.True(frame.X < left.X && frame.Right > right.Right);
        CheckPorts(doc, "ToCenter", "1", "0");
        CheckPorts(doc, "ToRight", "1", "0");
        CheckPorts(doc, "ToLeft", "0", "1");
        CheckRoutesAndNodes(doc);
    }

    [Fact]
    public void ServiceAndDependencyCanShareTheSameColumn()
    {
        var services = Services(3);
        services[0].DiagramColumn = -1;
        services[2].DiagramColumn = 1;
        services[2].Block = "Application";
        var analysis = Analysis(services,
            Link(services[0], "OnLeft", "SVC_VENDOR_HOST", "Vendor", block: "Vendor frame"),
            Link(services[2], "OnRight", "DATABASE_HOST", "DB", block: "Application"),
            Link(services[1], "Web", target: services[2]),
            Link(services[0], "Center", target: services[1]));
        var template = NetworkTableTemplates.BasicHtml.Replace("export-csv=false", "id=column-test|export-csv=false", StringComparison.Ordinal);
        var table = NetworkMapGenerator.RenderTable(template, analysis);
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var leftService = Bounds(doc, Cell(doc, services[0].Id));
        var rightService = Bounds(doc, Cell(doc, services[2].Id));
        var leftTarget = Bounds(doc, Assert.Single(Cells(doc, "value", "Vendor")));
        var rightTarget = Bounds(doc, Assert.Single(Cells(doc, "value", "DB")));
        Assert.Equal(leftService.X, leftTarget.X);
        Assert.Equal(rightService.X, rightTarget.X);
        Assert.True(leftService.Bottom < leftTarget.Y);
        Assert.True(rightService.Bottom < rightTarget.Y);
        CheckPorts(doc, "OnLeft", "1", "1");
        CheckPorts(doc, "OnRight", "1", "1");
        Assert.Equal(services[2].Id, Attr(Assert.Single(Cells(doc, "sourcePath", "Web")), "target"));
        CheckRoutesAndNodes(doc);

        var configuration = JsonSerializer.Deserialize<NetworkMapSettings>("""{"Services":[{"Label":"Legacy"}]}""")!;
        Assert.Equal(0, Assert.Single(configuration.Services).DiagramColumn);
        configuration.Services = [.. services];
        var restored = JsonSerializer.Deserialize<NetworkMapSettings>(JsonSerializer.Serialize(configuration))!;
        Assert.Equal(new[] { -1, 0, 1 }, restored.Services.Select(service => service.DiagramColumn));

        // Drawing again from the existing analysis uses the updated column.
        services[2].DiagramColumn = 2;
        var updated = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Assert.True(Bounds(updated, Cell(updated, services[2].Id)).X > Bounds(updated, Assert.Single(Cells(updated, "value", "DB"))).X);
        Assert.Equal(table, NetworkMapGenerator.RenderTable(template, analysis));
        CheckPorts(updated, "OnRight", "0", "1");
        CheckRoutesAndNodes(updated);
    }

    private static void CheckPorts(XDocument doc, string path, string exit, string entry)
    {
        var style = Style(Assert.Single(Cells(doc, "sourcePath", path)));
        Assert.Equal(exit, style["exitX"]);
        Assert.Equal(entry, style["entryX"]);
    }

    private static Box Bounds(XDocument doc, XElement item)
    {
        var parent = Attr(item, "parent");
        var origin = parent == "1" ? new Box(0, 0, 0, 0) : Bounds(doc, Cell(doc, parent));
        var geo = item.Element("mxGeometry")!;
        return new Box(origin.X + Value(geo, "x"), origin.Y + Value(geo, "y"), Value(geo, "width"), Value(geo, "height"));
    }

    private static void CheckRoutesAndNodes(XDocument doc)
    {
        var edges = Cells(doc, "edge", "1");
        var vertices = Cells(doc, "vertex", "1").Where(cell => Attr(cell, "kind") != "component-icon").ToArray();
        var boxes = vertices.ToDictionary(cell => Attr(cell, "id"), cell => Bounds(doc, cell));
        var obstacles = vertices.Select(cell =>
        {
            var box = boxes[Attr(cell, "id")];
            return Attr(cell, "kind") == "container" ?
                box with { H = double.Parse(Style(cell)["height"], CultureInfo.InvariantCulture) } : box;
        }).ToArray();
        for (var i = 0; i < obstacles.Length; i++)
            for (var j = i + 1; j < obstacles.Length; j++)
                Assert.False(obstacles[i].Overlaps(obstacles[j]), "Overlapping nodes or headers");

        var targets = edges.Select(edge => Attr(edge, "target")).ToHashSet(StringComparer.Ordinal);
        var routingObstacles = obstacles.Concat(Cells(doc, "kind", "container")
            .Select(frame => Attr(frame, "id")).Where(targets.Contains).Select(id => boxes[id])).ToArray();
        foreach (var link in edges)
        {
            var styles = Style(link);
            double S(string key) => double.Parse(styles[key], CultureInfo.InvariantCulture);
            var source = boxes[Attr(link, "source")];
            var target = boxes[Attr(link, "target")];
            var origin = boxes.GetValueOrDefault(Attr(link, "parent"), new Box(0, 0, 0, 0));
            var points = new List<(double X, double Y)> { (source.X + source.W * S("exitX"), source.Y + source.H * S("exitY")) };
            var waypoints = link.Element("mxGeometry")!.Element("Array")?.Elements("mxPoint") ?? [];
            points.AddRange(waypoints.Select(p => (origin.X + Value(p, "x"), origin.Y + Value(p, "y"))));
            points.Add((target.X + target.W * S("entryX"), target.Y + target.H * S("entryY")));
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var b = points[i];
                const double margin = .00001;
                var vertical = Math.Abs(a.X - b.X) < margin;
                Assert.True(vertical || Math.Abs(a.Y - b.Y) < margin, "Non-orthogonal route");
                foreach (var box in routingObstacles)
                {
                    var crosses = vertical
                        ? a.X > box.X + margin && a.X < box.Right - margin && Math.Max(a.Y, b.Y) > box.Y + margin && Math.Min(a.Y, b.Y) < box.Bottom - margin
                        : a.Y > box.Y + margin && a.Y < box.Bottom - margin && Math.Max(a.X, b.X) > box.X + margin && Math.Min(a.X, b.X) < box.Right - margin;
                    Assert.False(crosses, "Edge crosses a node, header or target frame");
                }
            }
        }
    }

    [Theory]
    [InlineData("BASE_URL", false)]
    [InlineData("SVC_PAYMENTS_API_URL", true)]
    public void SharedTargetHasAnIndependentArrowFromEachCaller(string parameter, bool onLeft)
    {
        var services = Services(2);
        var connections = services.Select((s, i) => Link(s, "Backend.Url", parameter, port: (8000 + i).ToString())).ToArray();
        var display = new NetworkDisplayOptions();
        var analysis = Analysis(services, connections) with { Display = display };
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));

        Assert.Equal(3, Cells(doc, "kind", "node").Length);
        var frame = Assert.Single(Cells(doc, "kind", "container"));
        Assert.Equal("k8s", Attr(frame, "value"));
        Assert.Empty(Cells(doc, "kind", "junction"));
        var target = Assert.Single(Cells(doc, "value", "Backend"));
        var arrows = Cells(doc, "target", Attr(target, "id"));
        Assert.Equal(services.Select(s => s.Id).Order(), arrows.Select(c => Attr(c, "source")).Order());
        Assert.All(arrows, c => Assert.Equal("block", Style(c)["endArrow"]));
        Assert.Equal(new[] { "8000 (HTTPS)", "8001 (HTTPS)" }, arrows.Select(c => Attr(c, "value")).Order());
        Assert.Equal(2, arrows.Select(c => Style(c)["strokeColor"]).Distinct().Count());
        var remainingColor = Style(arrows.Single(c => Attr(c, "source") == services[1].Id))["strokeColor"];
        Assert.All(services, service =>
        {
            var sx = Bounds(doc, Cell(doc, service.Id)).X;
            var tx = Bounds(doc, target).X;
            Assert.True(onLeft ? tx < sx : tx > sx);
        });
        CheckRoutesAndNodes(doc);

        display.ColorArrowsByService = false;
        var grayArrows = Cells(XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis)), "edge", "1");
        Assert.Equal(2, grayArrows.Length);
        Assert.All(grayArrows, c => Assert.Equal("#666666", Style(c)["strokeColor"]));
        display.ColorArrowsByService = true;

        connections[0].Choice.Include = false;
        doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Empty(Cells(doc, "kind", "junction"));
        var direct = Assert.Single(Cells(doc, "edge", "1"));
        Assert.Equal(services[1].Id, Attr(direct, "source"));
        Assert.Equal(Attr(target, "id"), Attr(direct, "target"));
        Assert.Equal(remainingColor, Style(direct)["strokeColor"]);
    }

    [Theory]
    [InlineData("SVC_KAFKA_HOST", "", 1d)]
    [InlineData("KAFKA_HOST", "", 0d)]
    [InlineData("KAFKA_HOST", "k8s", 0d)]
    [InlineData("SVC_KAFKA_HOST", "Region", 1d)]
    public void SingleIntegrationFrameAcceptsAnArrowFromEachCaller(string parameter, string parentBlock, double entryX)
    {
        var services = Services(2);
        var connections = services.Select((service, i) => Link(service, "Kafka.Host", parameter, "Топики Kafka",
            block: "Kafka", port: (9092 + i).ToString())).ToArray();
        foreach (var connection in connections) connection.ExternalTarget.ParentBlock = parentBlock;
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(Analysis(services, connections)));
        var frame = Assert.Single(Cells(doc, "value", "Kafka"));
        var node = Assert.Single(Cells(doc, "value", "Топики Kafka"));
        var frameId = Attr(frame, "id");
        Assert.Equal(frameId, Attr(node, "parent"));
        var frameBounds = Bounds(doc, frame);
        var nodeBounds = Bounds(doc, node);
        var arrows = Cells(doc, "edge", "1");
        Assert.Equal(services.Select(service => service.Id).Order(), arrows.Select(edge => Attr(edge, "source")).Order());
        var edgeParent = parentBlock == "k8s" ? Attr(frame, "parent") : "1";
        Assert.All(arrows, edge =>
        {
            Assert.Equal(frameId, Attr(edge, "target"));
            Assert.Equal(edgeParent, Attr(edge, "parent"));
            var style = Style(edge);
            Assert.Equal(entryX, double.Parse(style["entryX"], CultureInfo.InvariantCulture));
            var y = frameBounds.Y + frameBounds.H * double.Parse(style["entryY"], CultureInfo.InvariantCulture);
            Assert.InRange(y, nodeBounds.Y, nodeBounds.Bottom);
        });
        Assert.Equal(2, arrows.Select(edge => Style(edge)["entryY"]).Distinct().Count());
        CheckRoutesAndNodes(doc);
    }

    [Fact]
    public void OrderedIntegrationsUseNonCrossingLanesOnBothSides()
    {
        var services = Services(14);
        var connections = services.SelectMany((service, index) => new[]
        {
            Link(service, "Identity.Url", $"SVC_{index}_URL", $"Identity {services.Length - index}"),
            Link(service, "Storage.Host", $"STORAGE_{index}_HOST", $"Storage {services.Length - index}")
        }).ToArray();
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(Analysis(services, connections)));
        var sourceColors = Cells(doc, "edge", "1").GroupBy(edge => Attr(edge, "source"))
            .Select(group => Assert.Single(group.Select(edge => Style(edge)["strokeColor"]).Distinct())).ToArray();
        Assert.Equal(services.Length, sourceColors.Distinct().Count());

        var bottomOfServices = services.Max(service => Bounds(doc, Cell(doc, service.Id)).Bottom);
        foreach (var path in new[] { "Identity.Url", "Storage.Host" })
        {
            var left = path == "Identity.Url";
            double previousTargetY = bottomOfServices;
            double previousOffset = double.PositiveInfinity;
            foreach (var service in services.OrderBy(service => Bounds(doc, Cell(doc, service.Id)).Y))
            {
                var edge = Assert.Single(Cells(doc, "source", service.Id).Where(cell => Attr(cell, "sourcePath") == path));
                var source = Bounds(doc, Cell(doc, service.Id));
                var target = Bounds(doc, Cell(doc, Attr(edge, "target")));
                Assert.True(target.Y > previousTargetY);
                var parent = Bounds(doc, Cell(doc, Attr(edge, "parent")));
                var bends = edge.Element("mxGeometry")!.Element("Array")!.Elements("mxPoint").ToArray();
                Assert.Equal(2, bends.Length);
                var lane = parent.X + Value(bends[0], "x");
                Assert.Equal(Value(bends[0], "x"), Value(bends[1], "x"));
                Assert.True(Value(bends[0], "y") < Value(bends[1], "y"));
                Assert.True(left ? lane > target.Right && lane < source.X : lane > source.Right && lane < target.X);
                var offset = left ? source.X - lane : lane - source.Right;
                // With both endpoints in the same vertical order, outer lanes for the upper exits prevent crossings.
                Assert.True(offset < previousOffset);
                if (double.IsFinite(previousOffset)) Assert.Equal(14, previousOffset - offset);
                previousTargetY = target.Y;
                previousOffset = offset;
            }
        }
        CheckRoutesAndNodes(doc);
    }

    [Fact]
    public void NamedContainersAreGlobalAndRoutesAcrossTheirHeadersStayClear()
    {
        var s = Services(4);
        s[1].Block = " K8S ";
        s[2].Block = "k8s-2";
        s[3].Block = "";
        var connections = new[]
        {
            Link(s[0], "External.Url", "SVC_EXTERNAL_API_URL", "External"),
            Link(s[3], "External.Url", "SVC_EXTERNAL_API_URL", "External"),
            Link(s[1], "Database.Host", "DATABASE_HOST", "Database"),
            Link(s[2], "Cache.Host", "CACHE_HOST", "Cache", block: "k8s-2"),
            Link(s[3], "Queue.Host", "QUEUE_HOST", "Queue", block: "aaa"),
            Link(s[0], "Self", target:s[0]),
            Link(s[0], "B", target:s[1]),
            Link(s[0], "B2", target:s[1]),
            Link(s[1], "C", target:s[2]),
            Link(s[2], "A", target:s[0])
        };
        var analysis = Analysis(s, connections);
        var xml = NetworkMapGenerator.CreateDiagram(analysis);
        var doc = XDocument.Parse(xml);
        var frames = Cells(doc, "kind", "container");
        Assert.Equal(3, frames.Length);
        var k8s = frames.Single(c => Attr(c, "value").Equals("k8s", StringComparison.OrdinalIgnoreCase));
        var other = frames.Single(c => Attr(c, "value") == "k8s-2");
        Assert.False(Bounds(doc, k8s).Overlaps(Bounds(doc, other)));

        foreach (var node in new[] { Cell(doc, s[0].Id), Cell(doc, s[1].Id), Cells(doc, "value", "External").Single(), Cells(doc, "value", "Database").Single() })
            Assert.Equal(Attr(k8s, "id"), Attr(node, "parent"));
        Assert.Equal(Attr(other, "id"), Attr(Cell(doc, s[2].Id), "parent"));
        Assert.Equal("1", Attr(Cell(doc, s[3].Id), "parent"));
        var queueContainer = frames.Single(c => Attr(c, "value") == "aaa");
        Assert.True(Bounds(doc, queueContainer).Y > Bounds(doc, other).Bottom);

        var center = Bounds(doc, Cell(doc, s[0].Id)).X;
        Assert.True(Bounds(doc, Cells(doc, "value", "External").Single()).X < center);
        Assert.True(Bounds(doc, Cells(doc, "value", "Database").Single()).X > center);
        Assert.Single(Cells(doc, "source", s[0].Id).Where(edge => Attr(edge, "target") == s[1].Id));
        CheckRoutesAndNodes(doc);
        var reordered = analysis with { Services = s.Reverse().ToArray(), Connections = connections.Reverse().ToArray() };
        Assert.Equal(xml, NetworkMapGenerator.CreateDiagram(reordered));
    }

    [Fact]
    public void BlocksAdvanceOnlyTheColumnsTheyOccupy()
    {
        var service = Assert.Single(Services(1));
        var database = Link(service, "Database.Host", "DB_HOST", "DB", block: "db");
        var wso = Link(service, "Wso.Host", "WSO_HOST", "WSO PCR", block: "WSO PCR");
        var windows = Link(service, "Signing.Host", "SVC_SIGNING_HOST", new string('X', 420), block: "windows server");
        var analysis = Analysis([service], database, wso, windows);
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Box Frame(XDocument drawing, string label) => Bounds(drawing, Assert.Single(Cells(drawing, "kind", "container").Where(c => Attr(c, "value") == label)));

        var databaseFrame = Frame(doc, "db");
        var wsoFrame = Frame(doc, "WSO PCR");
        var windowsFrame = Frame(doc, "windows server");
        Assert.Equal(Frame(doc, "k8s").Y, databaseFrame.Y);
        Assert.Equal(databaseFrame.Y, windowsFrame.Y);
        Assert.True(wsoFrame.Y > databaseFrame.Bottom);
        Assert.True(wsoFrame.Y < windowsFrame.Bottom);
        CheckRoutesAndNodes(doc);

        windows.Choice.Include = false;
        var withoutWindows = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Equal(wsoFrame.Y, Frame(withoutWindows, "WSO PCR").Y);
        Assert.Equal(databaseFrame.Y, Frame(withoutWindows, "db").Y);
    }

    [Fact]
    public void NestedFramesInDifferentColumnsCanStartAtTheSameHeight()
    {
        var service = Assert.Single(Services(1));
        service.Block = "Services";
        service.ParentBlock = "Region";
        var database = Link(service, "Database.Host", "DB_HOST", "DB", block: "Databases");
        database.ExternalTarget.ParentBlock = "Region";
        var other = Link(service, "Queue.Host", "QUEUE_HOST", "Queue", block: "Queues");
        other.ExternalTarget.ParentBlock = "Other Region";
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(Analysis([service], database, other)));
        Box Frame(string label) => Bounds(doc, Assert.Single(Cells(doc, "value", label)));
        var services = Frame("Services");
        var databases = Frame("Databases");
        var region = Frame("Region");
        var otherRegion = Frame("Other Region");

        Assert.Equal(services.Y, databases.Y);
        Assert.False(services.Overlaps(databases));
        Assert.True(otherRegion.Y > region.Bottom);
        CheckRoutesAndNodes(doc);

        database.ExternalTarget.ParentBlock = "Other Region";
        doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(Analysis([service], database, other)));
        region = Frame("Region");
        otherRegion = Frame("Other Region");
        Assert.Equal(region.Y, otherRegion.Y);
        Assert.False(region.Overlaps(otherRegion));
        CheckRoutesAndNodes(doc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildBlocksKeepTheirNodesAndUseTheClosestCommonContainerForArrows(bool useColumnOffsets)
    {
        var s = Services(6);
        s[0].Block = s[1].Block = s[5].Block = "";
        s[0].ParentBlock = s[1].ParentBlock = "k8s";
        s[2].Block = "Kafka";
        s[2].ParentBlock = " k8s ";
        s[3].Block = " KAFKA ";
        s[4].Block = "Cache";
        s[4].ParentBlock = "k8s";
        if (useColumnOffsets)
        {
            s[0].DiagramColumn = -2;
            s[2].DiagramColumn = 2;
            s[3].DiagramColumn = 1;
            s[4].DiagramColumn = 2;
        }

        var links = new[]
        {
            Link(s[0], "ToKafka", target: s[2]), Link(s[2], "InsideKafka", target: s[3]),
            Link(s[3], "ToCache", target: s[4]), Link(s[4], "OutsideK8s", target: s[5]),
            Link(s[5], "BackToK8s", target: s[1])
        };
        if (useColumnOffsets)
        {
            var dependency = Link(s[3], "Load.Host", "LOAD_HOST", "Direct dependency", block: "");
            dependency.ExternalTarget.ParentBlock = "k8s";
            links = [.. links, dependency];
        }
        var analysis = Analysis(s, links);
        var xml = NetworkMapGenerator.CreateDiagram(analysis);
        var doc = XDocument.Parse(xml);
        var frames = Cells(doc, "kind", "container");
        Assert.Equal(3, frames.Length);
        var k8s = frames.Single(c => Attr(c, "value") == "k8s");
        var kafka = frames.Single(c => Attr(c, "value").Equals("Kafka", StringComparison.OrdinalIgnoreCase));
        var cache = frames.Single(c => Attr(c, "value") == "Cache");
        Assert.Equal("1", Attr(k8s, "parent"));
        var outer = Bounds(doc, k8s);
        foreach (var child in new[] { Cell(doc, s[0].Id), Cell(doc, s[1].Id), kafka, cache })
        {
            Assert.Equal(Attr(k8s, "id"), Attr(child, "parent"));
            var bounds = Bounds(doc, child);
            Assert.True(outer.X < bounds.X && bounds.Right < outer.Right);
            Assert.True(outer.Y + 44 < bounds.Y && bounds.Bottom < outer.Bottom);
        }
        Assert.False(Bounds(doc, kafka).Overlaps(Bounds(doc, cache)));
        Assert.Equal(Attr(kafka, "id"), Attr(Cell(doc, s[2].Id), "parent"));
        Assert.Equal(Attr(kafka, "id"), Attr(Cell(doc, s[3].Id), "parent"));
        Assert.Equal(Attr(cache, "id"), Attr(Cell(doc, s[4].Id), "parent"));
        Assert.Equal("1", Attr(Cell(doc, s[5].Id), "parent"));
        if (useColumnOffsets)
        {
            var dependency = Assert.Single(Cells(doc, "value", "Direct dependency"));
            Assert.Equal(Attr(k8s, "id"), Attr(dependency, "parent"));
            Assert.Equal(Bounds(doc, Cell(doc, s[3].Id)).X, Bounds(doc, dependency).X);
            Assert.True(Bounds(doc, Cell(doc, s[3].Id)).Bottom < Bounds(doc, dependency).Y);
        }
        string EdgeParent(string path) => Attr(Assert.Single(Cells(doc, "sourcePath", path)), "parent");
        Assert.Equal(Attr(kafka, "id"), EdgeParent("InsideKafka"));
        Assert.Equal(Attr(k8s, "id"), EdgeParent("ToKafka"));
        Assert.Equal(Attr(k8s, "id"), EdgeParent("ToCache"));
        Assert.Equal(s[4].Id, Attr(Assert.Single(Cells(doc, "sourcePath", "ToCache")), "target"));
        Assert.Equal("1", EdgeParent("OutsideK8s"));
        CheckRoutesAndNodes(doc);
        Assert.Equal(xml, NetworkMapGenerator.CreateDiagram(Analysis(s.Reverse().ToArray(), links.Reverse().ToArray())));
    }

    [Fact]
    public void ExcludingAConnectionKeepsItsSharedBlockParentWithoutItsNode()
    {
        var s = Assert.Single(Services(1));
        s.Block = "Kafka";
        var connection = Link(s, "Hidden.Host", block: "kafka");
        connection.ExternalTarget.ParentBlock = "k8s";
        connection.Choice.Include = false;

        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(Analysis([s], connection)));
        var outer = Assert.Single(Cells(doc, "value", "k8s"));
        var inner = Assert.Single(Cells(doc, "value", "Kafka"));
        Assert.Equal(Attr(outer, "id"), Attr(inner, "parent"));
        Assert.Equal(s.Id, Attr(Assert.Single(Cells(doc, "kind", "node")), "id"));
        Assert.Empty(Cells(doc, "edge", "1"));
    }

    [Theory]
    [InlineData("A", " a ", "B", "", "собственным", "A")]
    [InlineData("A", "B", " a ", "C", "разные родители", "B", "C")]
    [InlineData("A", "B", "B", "A", "цикл", "A", "B")]
    [InlineData("A", "B", "B", "C", "два уровня", "A", "B", "C")]
    public void InvalidHierarchyIsReportedByBothExports(string block, string parent, string otherBlock, string otherParent,
        string problem, params string[] names)
    {
        var s = Services(2);
        (s[0].Block, s[0].ParentBlock) = (block, parent);
        (s[1].Block, s[1].ParentBlock) = (otherBlock, otherParent);
        var analysis = Analysis(s);
        var diagram = Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.CreateDiagram(analysis));
        var page = Assert.Throws<InvalidDataException>(() => NetworkMapGenerator.RenderTable(NetworkTableTemplates.BasicHtml, analysis));
        Assert.Equal(diagram.Message, page.Message);
        Assert.Contains(problem, diagram.Message);
        Assert.All(names, name => Assert.Contains(name, diagram.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptionalCaptionsAppearOnDirectArrowsAndLongNodeNamesStayIntact()
    {
        var services = Services(2);
        var title = new string('A', 160);
        var a = Link(services[0], "GrpcClients.Payment.Host", name: title, port: "5432");
        var b = Link(services[1], "Payment.Url", name: title, port: "443");
        a.Choice.Protocol = "Unknown";
        var display = new NetworkDisplayOptions();
        var analysis = Analysis(services, a, b) with { Display = display };
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var node = Assert.Single(Cells(doc, "value", title));
        Assert.True(Bounds(doc, node).H > 110);
        Assert.Equal("5432", Attr(Assert.Single(Cells(doc, "source", a.Service.Id)), "value"));
        Assert.Equal("443 (HTTPS)", Attr(Assert.Single(Cells(doc, "source", b.Service.Id)), "value"));
        display.ShowConfigurationKeys = display.HideProtocolsOnArrows = true;
        doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Equal("5432\n" + a.Path, Attr(Assert.Single(Cells(doc, "source", a.Service.Id)), "value"));
        Assert.Equal("443\n" + b.Path, Attr(Assert.Single(Cells(doc, "source", b.Service.Id)), "value"));
        Assert.Equal(2, Cells(doc, "target", Attr(node, "id")).Length);
        CheckRoutesAndNodes(doc);
    }
}
