using System.Globalization;
using System.Xml.Linq;
using ServiceAutoPassport.Core;

namespace ServiceAutoPassport.Tests;

public class NetworkLayoutTests
{
    private static NetworkServiceInput[] Services(int count) => Enumerable.Range(0, count).Select(i =>
        new NetworkServiceInput { J2File = Path.Combine(Path.GetTempPath(), "layout", i.ToString(), "appsettings.deploy.j2"), Label = "Service " + i }).ToArray();
    private static NetworkConnection Edge(NetworkServiceInput source, string name, NetworkServiceInput? target = null) => new()
    {
        Service = source, Path = name, Host = "{{ SHARED_HOST }}",
        Choice = new NetworkChoice { Label = "Shared endpoint with a long parameter name", Protocol = "HTTPS", TargetService = target?.Id ?? "" }
    };
    private static Dictionary<string, (double X, double Y, double W, double H)> Boxes(XDocument doc) =>
        doc.Descendants("mxCell").Where(c => (string?)c.Attribute("vertex") == "1").ToDictionary(c => (string)c.Attribute("id")!, c =>
        {
            var g = c.Element("mxGeometry")!;
            double V(string n) => double.Parse((string)g.Attribute(n)!, CultureInfo.InvariantCulture);
            return (V("x"), V("y"), V("width"), V("height"));
        });
    private static void CheckRoutesAndNodes(XDocument doc)
    {
        var boxes = Boxes(doc);
        foreach (var a in boxes)
            foreach (var b in boxes.Where(b => string.CompareOrdinal(b.Key, a.Key) > 0))
                Assert.False(a.Value.X < b.Value.X + b.Value.W && a.Value.X + a.Value.W > b.Value.X &&
                    a.Value.Y < b.Value.Y + b.Value.H && a.Value.Y + a.Value.H > b.Value.Y, "Overlapping nodes");
        foreach (var edge in doc.Descendants("mxCell").Where(c => (string?)c.Attribute("edge") == "1"))
        {
            var styles = ((string)edge.Attribute("style")!).Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            double S(string key) => double.Parse(styles[key], CultureInfo.InvariantCulture);
            var source = boxes[(string)edge.Attribute("source")!]; var target = boxes[(string)edge.Attribute("target")!];
            var points = new List<(double X, double Y)> { (source.X + source.W * S("exitX"), source.Y + source.H * S("exitY")) };
            points.AddRange(edge.Descendants("mxPoint").Select(p => (double.Parse((string)p.Attribute("x")!, CultureInfo.InvariantCulture), double.Parse((string)p.Attribute("y")!, CultureInfo.InvariantCulture))));
            points.Add((target.X + target.W * S("entryX"), target.Y + target.H * S("entryY")));
            for (var i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                Assert.True(a.X == b.X || Math.Abs(a.Y - b.Y) < .000001, "Non-orthogonal route");
                foreach (var box in boxes.Values)
                {
                    var crosses = a.X == b.X
                        ? a.X > box.X && a.X < box.X + box.W && Math.Max(a.Y, b.Y) > box.Y && Math.Min(a.Y, b.Y) < box.Y + box.H
                        : a.Y > box.Y && a.Y < box.Y + box.H && Math.Max(a.X, b.X) > box.X && Math.Min(a.X, b.X) < box.X + box.W;
                    Assert.False(crosses, "Edge crosses node interior");
                }
            }
        }
    }
    [Fact]
    public void SelectedServicesStayInCenterWithClearStableRoutes()
    {
        var services = Services(12);
        var edges = Enumerable.Range(0, 9).Select(i => Edge(services[i], "Next", services[i + 1])).ToList();
        edges.Add(Edge(services[0], "Skip", services[6]));
        edges.Add(Edge(services[0], "Shared")); edges.Add(Edge(services[8], "Shared"));
        edges.Add(Edge(services[10], "Separate", services[11]));
        var analysis = new NetworkAnalysis(services, new[] { "prod" }, edges, Array.Empty<string>());
        var xml = NetworkMapGenerator.CreateDiagram(analysis);
        var doc = XDocument.Parse(xml); var boxes = Boxes(doc);
        Assert.Single(services.Select(s => boxes[s.Id].X).Distinct());
        Assert.True(boxes.Single(b => !services.Any(s => s.Id == b.Key)).Value.X > boxes[services[0].Id].X);
        CheckRoutesAndNodes(doc);
        var reordered = new NetworkAnalysis(services.Reverse().ToArray(), analysis.Environments, edges.AsEnumerable().Reverse().ToArray(), analysis.Diagnostics);
        Assert.Equal(xml, NetworkMapGenerator.CreateDiagram(reordered));
    }
    [Theory]
    [InlineData("BASE_URL", false)]
    [InlineData("SVC_PAYMENTS_API", true)]
    public void TwoCallersReuseOneParameterBlock(string parameter, bool onLeft)
    {
        var services = Services(2);
        var edges = services.Select((s, i) => new NetworkConnection
        {
            Service = s, Path = "Backend.Url", Host = "{{ " + parameter + " }}", Port = (8000 + i).ToString(),
            Choice = new NetworkChoice { Label = "Backend", Protocol = "HTTP" }
        }).ToArray();
        var analysis = new NetworkAnalysis(services, Array.Empty<string>(), edges, Array.Empty<string>());
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var boxes = Boxes(doc);
        Assert.Equal(3, boxes.Count);
        var links = doc.Descendants("mxCell").Where(c => (string?)c.Attribute("edge") == "1").ToArray();
        Assert.Equal(2, links.Length);
        var target = Assert.Single(links.Select(c => (string)c.Attribute("target")!).Distinct());
        Assert.Equal(services.Select(s => s.Id).Order(), links.Select(c => (string)c.Attribute("source")!).Order());
        Assert.All(services, s => Assert.True(onLeft ? boxes[target].X < boxes[s.Id].X : boxes[target].X > boxes[s.Id].X));
        Assert.DoesNotContain("(+1)", (string)doc.Descendants("mxCell").Single(c => (string?)c.Attribute("id") == target).Attribute("value")!);
        CheckRoutesAndNodes(doc);
    }
    [Fact]
    public void SvcPrefixControlsDependencyColumnAndSelectedTargetsStayInCenter()
    {
        var services = Services(3);
        services[1].AddressVariables = "SVC_SELECTED_API";
        var edges = new[]
        {
            new NetworkConnection { Service = services[0], Path = "External.Url", Host = "https://{{ SVC_EXTERNAL_API }}/api",
                Choice = new NetworkChoice { Label = "External" } },
            new NetworkConnection { Service = services[1], Path = "Database.Host", Host = "{{ DATABASE_HOST }}",
                Choice = new NetworkChoice { Label = "Database" } },
            new NetworkConnection { Service = services[0], Path = "Selected.Url", Host = "{{ SVC_SELECTED_API }}",
                Choice = new NetworkChoice { Label = "Selected", TargetService = services[1].Id } },
            new NetworkConnection { Service = services[2], Path = "Other.Url", Host = "https://svc-literal.example.com",
                Choice = new NetworkChoice { Label = "Literal" } }
        };
        var analysis = new NetworkAnalysis(services, Array.Empty<string>(), edges, Array.Empty<string>(),
            new NetworkDisplayOptions { HideParameterNames = true });
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var boxes = Boxes(doc);
        Assert.Equal(6, boxes.Count);
        var center = boxes[services[0].Id].X;
        Assert.All(services, s => Assert.Equal(center, boxes[s.Id].X));
        var vertices = doc.Descendants("mxCell").Where(c => (string?)c.Attribute("vertex") == "1").ToArray();
        string Id(string label) => (string)vertices.Single(v => (string?)v.Attribute("value") == label).Attribute("id")!;
        Assert.True(boxes[Id("External")].X < center);
        Assert.True(boxes[Id("Database")].X > center);
        Assert.True(boxes[Id("Literal")].X > center);
        CheckRoutesAndNodes(doc);
    }
    [Fact]
    public void CyclesParallelEdgesAndSelfLoopsStayOutsideNodes()
    {
        var services = Services(4);
        services[0].Label = new string('A', 180);
        var edges = new[] { Edge(services[0], "B", services[1]), Edge(services[1], "A", services[0]),
            Edge(services[0], "Self", services[0]), Edge(services[0], "B2", services[1]), Edge(services[1], "C", services[2]) };
        var analysis = new NetworkAnalysis(services, Array.Empty<string>(), edges, Array.Empty<string>());
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        CheckRoutesAndNodes(doc);
        Assert.True(Boxes(doc)[services[0].Id].H > 110);
        Assert.Equal(services.Length, Boxes(doc).Count);
    }

    [Fact]
    public void DisplayFlagsChangeLabelsWithoutRemovingConnections()
    {
        var services = Services(1);
        var edge = Edge(services[0], "GrpcClients.Payment.Host");
        edge.Choice.Label = "Payment API";
        edge.Choice.Protocol = "Unknown";
        var display = new NetworkDisplayOptions { HideUnknownProtocols = true, HideParameterNames = true, ShowConfigurationKeys = false };
        var analysis = new NetworkAnalysis(services, Array.Empty<string>(), new[] { edge }, Array.Empty<string>(), display);
        var doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        Assert.Single(doc.Descendants("mxCell").Where(c => (string?)c.Attribute("edge") == "1"));
        Assert.Contains(doc.Descendants("mxCell"), c => (string?)c.Attribute("value") == "Payment API");
        Assert.Equal("", (string?)doc.Descendants("mxCell").Single(c => (string?)c.Attribute("edge") == "1").Attribute("value"));

        display.ShowConfigurationKeys = true;
        display.HideUnknownProtocols = false;
        display.HideParameterNames = false;
        doc = XDocument.Parse(NetworkMapGenerator.CreateDiagram(analysis));
        var caption = (string)doc.Descendants("mxCell").Single(c => (string?)c.Attribute("edge") == "1").Attribute("value")!;
        Assert.Contains("GrpcClients.Payment.Host", caption);
        Assert.Contains("Unknown", caption);
        Assert.Contains(doc.Descendants("mxCell"), c => ((string?)c.Attribute("value"))?.Contains("SHARED_HOST") == true);
    }
}
