using System.Xml.Linq;

namespace ServiceAutoPassport.Core;

internal enum DiagramColumn { SvcDependencies, Services, OtherDependencies }
internal sealed record DiagramVertex(string Id, string Label, DiagramColumn Column);
internal sealed record DiagramLink(string Id, string Source, string Target, string Label, string Path);

/// <summary>Three-column layout with shared dependencies and orthogonal routes outside node rectangles.</summary>
internal static class NetworkDiagramLayout
{
    private sealed record Box(double X, double Y, double Width, double Height);
    private static string Wrap(string label) => string.Join("\n", label.Split('\n').SelectMany(line =>
        Enumerable.Range(0, Math.Max(1, (line.Length + 33) / 34)).Select(i => line.Substring(i * 34, Math.Min(34, line.Length - i * 34)))));

    public static string Render(IEnumerable<DiagramVertex> inputVertices, IEnumerable<DiagramLink> inputLinks)
    {
        var vertices = inputVertices.OrderBy(v => v.Label, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).ToArray();
        var links = inputLinks.OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal)
            .ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var columns = vertices.ToDictionary(v => v.Id, v => v.Column);
        // Allocate separate ports on each side, including both ends of self loops.
        int ExitSide(DiagramLink edge) => columns[edge.Target] < columns[edge.Source] ? 0 : 1;
        int EntrySide(DiagramLink edge) => columns[edge.Source] < columns[edge.Target] ? 0 : 1;
        var ports = vertices.SelectMany(v => new[] { 0, 1 }.Select(side => (v.Id, Side: side)))
            .ToDictionary(p => p, p => links.SelectMany(edge =>
                new[] { (edge.Id, Source: true, Vertex: edge.Source, Side: ExitSide(edge)),
                    (edge.Id, Source: false, Vertex: edge.Target, Side: EntrySide(edge)) })
                .Where(end => end.Vertex == p.Id && end.Side == p.Side).Select(end => (end.Id, end.Source)).ToArray());
        double Port(DiagramLink edge, bool source)
        {
            var ends = ports[(source ? edge.Source : edge.Target, source ? ExitSide(edge) : EntrySide(edge))];
            return (Array.IndexOf(ends, (edge.Id, source)) + 1.0) / (ends.Length + 1);
        }
        var labels = vertices.ToDictionary(v => v.Id, v => Wrap(v.Label));
        double Height(string id) => Math.Max(110, Math.Max(labels[id].Split('\n').Length * 18 + 30,
            Math.Max(ports[(id, 0)].Length, ports[(id, 1)].Length) * 18 + 30));
        var services = vertices.Where(v => v.Column == DiagramColumn.Services).Select(v => v.Id).ToArray();
        var serviceOrder = services.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        // Keep shared dependencies near the average position of their callers.
        double CallerPosition(string id) => links.Where(e => e.Target == id).Select(e => (double)serviceOrder[e.Source]).DefaultIfEmpty(0).Average();
        var layers = Enum.GetValues<DiagramColumn>().Select(column => column == DiagramColumn.Services ? services :
            vertices.Where(v => v.Column == column).OrderBy(v => CallerPosition(v.Id))
                .ThenBy(v => v.Label, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).Select(v => v.Id).ToArray()).ToArray();
        double LayerHeight(string[] layer) => layer.Sum(Height) + Math.Max(0, layer.Length - 1) * 70;
        var maxHeight = layers.Max(LayerHeight);
        // Every link has its own channel in one of the two inter-column gaps.
        var gap = 150 + links.Length * 14;
        const double width = 280;
        var boxes = new Dictionary<string, Box>();
        for (var column = 0; column < layers.Length; column++)
        {
            var layer = layers[column];
            var y = 40 + (maxHeight - LayerHeight(layer)) / 2;
            foreach (var id in layer)
            {
                boxes[id] = new Box(40 + column * (width + gap), y, width, Height(id));
                y += Height(id) + 70;
            }
        }
        var root = new XElement("root", new XElement("mxCell", new XAttribute("id", "0")),
            new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")));
        foreach (var vertex in vertices)
        {
            var box = boxes[vertex.Id];
            root.Add(new XElement("mxCell", new XAttribute("id", vertex.Id), new XAttribute("value", labels[vertex.Id]),
                new XAttribute("vertex", "1"), new XAttribute("parent", "1"),
                new XAttribute("style", "rounded=1;whiteSpace=wrap;html=0;align=center;verticalAlign=middle;spacing=12;fontSize=12;fillColor=" + (vertex.Column == DiagramColumn.Services ? "#dae8fc" : "#d5e8d4") + ";"),
                new XElement("mxGeometry", new XAttribute("x", box.X), new XAttribute("y", box.Y),
                    new XAttribute("width", box.Width), new XAttribute("height", box.Height), new XAttribute("as", "geometry"))));
        }
        for (var i = 0; i < links.Length; i++)
        {
            var edge = links[i]; var source = boxes[edge.Source]; var target = boxes[edge.Target];
            var exit = Port(edge, true); var entry = Port(edge, false);
            var sy = source.Y + source.Height * exit; var ty = target.Y + target.Height * entry;
            var channel = ExitSide(edge) == 0 ? source.X - 35 - i * 12 : source.X + source.Width + 35 + i * 12;
            var points = new XElement("Array", new XAttribute("as", "points"));
            void Point(double x, double y) => points.Add(new XElement("mxPoint", new XAttribute("x", x), new XAttribute("y", y)));
            Point(channel, sy); Point(channel, ty);
            root.Add(new XElement("mxCell", new XAttribute("id", edge.Id), new XAttribute("source", edge.Source),
                new XAttribute("target", edge.Target), new XAttribute("value", edge.Label), new XAttribute("sourcePath", edge.Path),
                new XAttribute("edge", "1"), new XAttribute("parent", "1"),
                new XAttribute("style", $"edgeStyle=orthogonalEdgeStyle;rounded=1;html=0;endArrow=block;exitX={ExitSide(edge)};exitY={exit.ToString(System.Globalization.CultureInfo.InvariantCulture)};entryX={EntrySide(edge)};entryY={entry.ToString(System.Globalization.CultureInfo.InvariantCulture)};exitPerimeter=1;entryPerimeter=1;fontSize=10;labelBackgroundColor=#ffffff;"),
                new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"), points)));
        }
        return new XElement("mxGraphModel", new XAttribute("grid", "1"), new XAttribute("gridSize", "10"), new XAttribute("page", "0"), root).ToString();
    }
}
