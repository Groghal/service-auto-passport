using System.Net;
using System.Xml.Linq;

namespace ServiceAutoPassport.Core;

internal enum DiagramColumn { SvcDependencies, Services, OtherDependencies }
internal sealed record DiagramVertex(string Id, string Label, DiagramColumn Column, string Block, string ParentBlock)
{
    public string Container => Block.Length == 0 ? ParentBlock : Block;
}
internal sealed record DiagramLink(string Id, string Source, string Target, string ConfigurationLabel, string Path, string TransportLabel);

internal readonly record struct DiagramPoint(double X, double Y);
internal readonly record struct DiagramBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public DiagramBox Expand(double by) => new(X - by, Y - by, Width + 2 * by, Height + 2 * by);
}

internal static class NetworkDiagramLayout
{
    private const double NodeWidth = 280;
    private const double MinimumColumnGap = 164;
    private const double FramePadding = 56;
    private const double Stub = 12;
    private const double LaneStep = 14;
    private const double LaneInset = 26;
    private const double RowGap = 46;
    private const double BandGap = 60;
    private const double HeaderHeight = 44;
    private const string OutlineColor = "#666666";
    private const string LabelColor = "#1c2a4c";
    private const string ComponentStyle = "shape=rectangle;rounded=0;whiteSpace=wrap;html=1;align=center;verticalAlign=middle;" +
        "spacingLeft=32;spacingRight=32;fontSize=13;fillColor=#fce7cd;strokeColor=#d79b00;fontColor=" + LabelColor + ";dropTarget=0;";
    private static readonly string[] ArrowColors =
    [
        "#005DA4", "#B44400", "#007D63", "#A52C91", "#8C6400", "#5439A1",
        "#00748D", "#B02C3B", "#427700", "#8A4261", "#65432A", "#445A70"
    ];

    private sealed record Frame(string Id, string Label, DiagramBox Box, Frame? Parent);
    private sealed record Node(string Id, DiagramBox Box, Frame? Frame, string Label);
    private sealed record Band(string Name, DiagramVertex[] Vertices, Band[] Children)
    {
        public IEnumerable<DiagramVertex> AllVertices => Vertices.Concat(Children.SelectMany(child => child.AllVertices));
        public bool HasServices => AllVertices.Any(vertex => vertex.Column == DiagramColumn.Services);
    }
    private sealed record PositionedBand(Band Band, double Top, double Bottom, PositionedBand[] Children);
    private sealed record Row(double Top, double Height)
    {
        public double At(double fraction) => Top + Height * fraction;
    }
    private sealed record Wire(DiagramLink Link, DiagramColumn Column, int Exit, int Entry);
    private sealed record Port(string CellId, DiagramBox Box, Frame? Parent, double X, double Y)
    {
        public DiagramPoint Position => new(Box.X + Box.Width * X, Box.Y + Box.Height * Y);
        public DiagramPoint Anchor => new(Position.X + (X == 0 ? -Stub : Stub), Position.Y);
    }
    private sealed record Route(Wire Wire, Port Start, Port End);

    public static string Render(IEnumerable<DiagramVertex> inputVertices, IEnumerable<DiagramLink> inputLinks,
        IReadOnlyDictionary<string, string> blockParents, bool colorArrowsByService)
    {
        var vertices = inputVertices.OrderBy(v => v.Label, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).ToArray();
        var links = inputLinks.OrderBy(e => e.Target, StringComparer.Ordinal).ThenBy(e => e.Source, StringComparer.Ordinal).ToArray();
        // Include services without visible arrows so changing the selection does not recolor other services.
        var serviceColors = colorArrowsByService
            ? vertices.Where(v => v.Column == DiagramColumn.Services).Select(v => v.Id).Order(StringComparer.Ordinal)
                .Zip(ArrowPalette().Distinct(StringComparer.Ordinal), (id, color) => (id, color))
                .ToDictionary(pair => pair.id, pair => pair.color, StringComparer.Ordinal)
            : null;
        var byId = vertices.ToDictionary(v => v.Id);
        var incoming = links.ToLookup(e => e.Target);
        var outgoing = links.ToLookup(e => e.Source);
        var names = vertices.Select(v => v.Container).Concat(blockParents.Values)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(StringComparer.Ordinal)!, StringComparer.OrdinalIgnoreCase);
        var members = vertices.ToLookup(v => v.Container, StringComparer.OrdinalIgnoreCase);
        var children = blockParents.ToLookup(pair => pair.Value, pair => names[pair.Key], StringComparer.OrdinalIgnoreCase);
        static IOrderedEnumerable<Band> OrderBands(IEnumerable<Band> bands) => bands.OrderByDescending(band => band.HasServices)
            .ThenBy(band => band.Name.Length == 0).ThenBy(band => band.Name, StringComparer.OrdinalIgnoreCase);
        var bands = OrderBands(names.Values.Where(name => name.Length > 0 && !blockParents.ContainsKey(name))
            .Select(name => new Band(name, members[name].ToArray(), OrderBands(children[name]
                .Select(child => new Band(child, members[child].ToArray(), []))).ToArray()))
            .Concat(members[""].GroupBy(vertex => vertex.Column).Select(column => new Band("", column.ToArray(), [])))).ToArray();
        var targetsOnFrame = bands.SelectMany(band => band.Children.Prepend(band))
            .Where(band => band.Name.Length != 0 && band.Children.Length == 0 &&
                band.Vertices.Length == 1 && band.Vertices[0].Column != DiagramColumn.Services)
            .Select(band => band.Vertices[0].Id).ToHashSet(StringComparer.Ordinal);

        var serviceOrder = bands.SelectMany(band => band.AllVertices.Where(v => v.Column == DiagramColumn.Services))
            .Select((service, index) => (service.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        double CallerPosition(DiagramVertex v) => incoming[v.Id]
            .Select(link => (double)serviceOrder[link.Source]).DefaultIfEmpty(0).Average();

        var passages = new List<double>();
        var rows = new Dictionary<string, Row>();
        var positionedBands = PlaceBands(bands, [42, 42, 42]);

        PositionedBand[] PlaceBands(Band[] siblings, double[] nextY)
        {
            var positioned = new List<PositionedBand>(siblings.Length);
            foreach (var band in siblings.OrderByDescending(band => band.HasServices)
                         .ThenBy(band => band.HasServices ? 0 : band.AllVertices.Average(CallerPosition)))
            {
                // A shared frame also occupies the columns between its leftmost and rightmost nodes.
                var columns = band.AllVertices.Select(vertex => (int)vertex.Column).ToArray();
                var first = columns.Min();
                var last = columns.Max();
                var position = PlaceBand(band, nextY[first..(last + 1)].Max());
                positioned.Add(position);
                for (var column = first; column <= last; column++)
                    nextY[column] = position.Bottom + BandGap;
            }
            passages.AddRange(nextY.Select(y => y - 18));
            return positioned.ToArray();
        }

        PositionedBand PlaceBand(Band band, double bandTop)
        {
            passages.Add(bandTop - 18);
            var named = band.Name.Length > 0;
            var firstY = bandTop + (named ? HeaderHeight + 26 : 0);
            double[] nextY = [firstY, firstY, firstY];
            var bottom = firstY;
            var integrationY = firstY;
            foreach (var column in band.Vertices.GroupBy(vertex => vertex.Column)
                         .OrderByDescending(column => column.Key == DiagramColumn.Services))
            {
                var isServiceColumn = column.Key == DiagramColumn.Services;
                var end = PlaceRows(isServiceColumn ? column : column.OrderBy(CallerPosition), integrationY);
                if (isServiceColumn) integrationY = end + RowGap;
                nextY[(int)column.Key] = end + BandGap;
                bottom = Math.Max(bottom, end);
            }
            var nested = PlaceBands(band.Children, nextY);
            bottom = Math.Max(bottom, nested.Select(child => child.Bottom).DefaultIfEmpty(firstY).Max());
            return new PositionedBand(band, bandTop, bottom + (named ? 26 : 0), nested);
        }

        double PlaceRows(IEnumerable<DiagramVertex> column, double y)
        {
            var bottom = y;
            foreach (var vertex in column)
            {
                var visiblePorts = outgoing[vertex.Id].Count() + incoming[vertex.Id].Count();
                var estimatedLines = Math.Max(1, (vertex.Label.Length + 29) / 30);
                var height = Math.Max(36, Math.Max(18 * estimatedLines + 16, 14 * (visiblePorts + 1)));
                rows.Add(vertex.Id, new Row(y, height));
                bottom = y + height;
                y = bottom + RowGap;
            }
            return bottom;
        }

        var wires = links.Select(link =>
        {
            var column = byId[link.Target].Column;
            return new Wire(link, column, column == DiagramColumn.SvcDependencies ? 0 : 1,
                column == DiagramColumn.OtherDependencies ? 0 : 1);
        }).ToArray();
        var ends = wires.SelectMany(w => new[]
        {
            (Node: w.Link.Source, Side: w.Exit, w.Link.Id, Source: true, Other: w.Link.Target),
            (Node: w.Link.Target, Side: w.Entry, w.Link.Id, Source: false, Other: w.Link.Source)
        }).ToLookup(end => (end.Node, end.Side));
        var fractions = new Dictionary<(string Id, bool Source), double>();
        foreach (var side in ends)
        {
            var ordered = side.OrderBy(end => rows[end.Other].At(.5))
                .ThenBy(end => end.Id, StringComparer.Ordinal).ThenBy(end => end.Source).ToArray();
            for (var i = 0; i < ordered.Length; i++)
                fractions[(ordered[i].Id, ordered[i].Source)] = (i + 1.0) / (ordered.Length + 1);
        }

        double StartY(Wire wire) => rows[wire.Link.Source].At(fractions[(wire.Link.Id, true)]);
        double EndY(Wire wire) => rows[wire.Link.Target].At(fractions[(wire.Link.Id, false)]);

        var laneByEdge = new Dictionary<string, int>();
        double ReserveLanes(DiagramColumn side, double minimumGap)
        {
            var sideWires = wires.Where(w => w.Column == side).ToArray();
            // Process lower exits first so that higher exits take the outer lanes when their spans overlap.
            var starts = new List<double>();
            foreach (var wire in sideWires.Where(w => StartY(w) < EndY(w))
                         .OrderByDescending(StartY).ThenBy(w => w.Link.Id, StringComparer.Ordinal))
            {
                var lane = starts.FindIndex(start => EndY(wire) + LaneStep <= start);
                if (lane < 0)
                {
                    lane = starts.Count;
                    starts.Add(StartY(wire));
                }
                else starts[lane] = StartY(wire);
                laneByEdge.Add(wire.Link.Id, lane);
            }

            var targetPadding = sideWires.Any(w => targetsOnFrame.Contains(w.Link.Target)) ? FramePadding : 0;
            return Math.Max(minimumGap, targetPadding + 2 * LaneInset + (starts.Count - 1) * LaneStep);
        }

        var columnPadding = new double[3];
        foreach (var band in bands)
        {
            if (band.Name.Length == 0) continue;
            foreach (var vertex in band.Vertices)
                columnPadding[(int)vertex.Column] = Math.Max(columnPadding[(int)vertex.Column], FramePadding);
            foreach (var vertex in band.Children.SelectMany(child => child.Vertices))
                columnPadding[(int)vertex.Column] = 2 * FramePadding;
        }
        double Gap(int left) => Math.Max(MinimumColumnGap, columnPadding[left] + columnPadding[left + 1] + 2 * Stub);
        var centerX = 140 + NodeWidth + ReserveLanes(DiagramColumn.SvcDependencies, Gap(0));
        double[] columnXs = [140, centerX, centerX + NodeWidth + ReserveLanes(DiagramColumn.OtherDependencies, Gap(1))];
        var frames = new List<Frame>();
        var nodes = new Dictionary<string, Node>();
        foreach (var position in positionedBands) AddBand(position, null);

        void AddBand(PositionedBand position, Frame? parent)
        {
            var band = position.Band;
            Frame? frame = null;
            if (band.Name.Length > 0)
            {
                var columns = band.Vertices.Select(vertex => (X: columnXs[(int)vertex.Column], Padding: FramePadding))
                    .Concat(band.Children.SelectMany(child => child.Vertices)
                        .Select(vertex => (X: columnXs[(int)vertex.Column], Padding: 2 * FramePadding))).ToArray();
                var left = columns.Min(column => column.X - column.Padding);
                var right = columns.Max(column => column.X + NodeWidth + column.Padding);
                frame = new Frame(NetworkMapGenerator.Id("container:" + band.Name.ToUpperInvariant()), band.Name,
                    new DiagramBox(left, position.Top, right - left, position.Bottom - position.Top), parent);
                frames.Add(frame);
            }

            foreach (var vertex in band.Vertices)
            {
                var row = rows[vertex.Id];
                nodes[vertex.Id] = new Node(vertex.Id, new DiagramBox(columnXs[(int)vertex.Column], row.Top, NodeWidth, row.Height),
                    frame, vertex.Label);
            }
            foreach (var child in position.Children) AddBand(child, frame);
        }

        var routes = wires.Select(wire =>
        {
            var source = nodes[wire.Link.Source];
            var target = nodes[wire.Link.Target];
            var start = new Port(source.Id, source.Box, source.Frame, wire.Exit, fractions[(wire.Link.Id, true)]);
            var end = target.Frame is { } frame && targetsOnFrame.Contains(target.Id)
                ? new Port(frame.Id, frame.Box, frame.Parent, wire.Entry, (EndY(wire) - frame.Box.Y) / frame.Box.Height)
                : new Port(target.Id, target.Box, target.Frame, wire.Entry, fractions[(wire.Link.Id, false)]);
            return new Route(wire, start, end);
        }).ToArray();

        double LaneX(Route route, int lane)
        {
            var offset = LaneInset + lane * LaneStep;
            return route.Wire.Exit == 0 ? centerX - offset : centerX + NodeWidth + offset;
        }

        var targetCells = routes.Select(route => route.End.CellId).ToHashSet(StringComparer.Ordinal);
        var frameObstacles = frames.Select(frame =>
            (targetCells.Contains(frame.Id) ? frame.Box : frame.Box with { Height = HeaderHeight }).Expand(4)).ToArray();
        var obstacles = nodes.Values.Select(node => node.Box.Expand(4)).Concat(frameObstacles).ToArray();
        var anchors = routes.SelectMany(r => new[] { r.Start.Anchor, r.End.Anchor }).ToArray();
        var laneXs = routes.Where(route => laneByEdge.ContainsKey(route.Wire.Link.Id))
            .Select(route => LaneX(route, laneByEdge[route.Wire.Link.Id]));
        var xs = anchors.Select(p => p.X).Concat(RoutingLanes(columnXs)).Concat(laneXs);
        if (frames.Any(frame => frame.Parent is not null || targetCells.Contains(frame.Id)))
            xs = xs.Concat(frameObstacles.SelectMany(box => new[] { box.X - 6, box.Right + 6 }));
        var ys = anchors.Select(p => p.Y).Concat(passages)
            .Concat(obstacles.SelectMany(box => new[] { box.Y - 6, box.Bottom + 6 }));
        var router = new NetworkDiagramRouter(xs, ys, obstacles);

        var paths = new Dictionary<string, DiagramPoint[]>();
        foreach (var route in routes)
        {
            if (!laneByEdge.TryGetValue(route.Wire.Link.Id, out var lane)) continue;
            if (router.TryRouteViaLane(route.Start.Anchor, route.End.Anchor, LaneX(route, lane)) is { } path)
                paths.Add(route.Wire.Link.Id, path);
        }

        var root = new XElement("root", new XElement("mxCell", new XAttribute("id", "0")),
            new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")));
        foreach (var frame in frames)
        {
            var headerWidth = Math.Min(frame.Box.Width, Math.Max(160, 8.0 * frame.Label.Length + 32));
            root.Add(Cell(frame.Id, "container", frame.Parent?.Id ?? "1", frame.Label, RelativeBox(frame.Box, frame.Parent),
                FormattableString.Invariant($"shape=umlFrame;container=1;collapsible=0;recursiveResize=0;pointerEvents=0;width={headerWidth};height={HeaderHeight};html=1;whiteSpace=wrap;align=center;verticalAlign=middle;fontSize=13;fillColor=#ffffff;strokeColor={OutlineColor};fontColor={LabelColor};")));
        }
        foreach (var node in nodes.Values)
        {
            root.Add(Cell(node.Id, "node", node.Frame?.Id ?? "1", node.Label,
                RelativeBox(node.Box, node.Frame), ComponentStyle));
            root.Add(ComponentIcon(node.Id));
        }

        foreach (var route in routes)
        {
            if (!paths.TryGetValue(route.Wire.Link.Id, out var path))
                path = router.Route(route.Start.Anchor, route.End.Anchor);
            root.Add(Edge(route, path, serviceColors?[route.Wire.Link.Source] ?? OutlineColor));
        }
        return new XElement("mxGraphModel", new XAttribute("grid", "1"), new XAttribute("gridSize", "10"),
            new XAttribute("page", "0"), root).ToString();
    }

    private static IEnumerable<string> ArrowPalette()
    {
        foreach (var color in ArrowColors) yield return color;

        // Spread extra colors across a dark RGB range for contrast against the canvas.
        for (uint index = 0; index < (1u << 21); index++)
        {
            var rgb = unchecked(index * 0x9E3779B9u);
            yield return $"#{16 + (rgb & 127):X2}{16 + ((rgb >> 7) & 127):X2}{16 + ((rgb >> 14) & 127):X2}";
        }
    }

    private static IEnumerable<double> RoutingLanes(double[] columnXs)
    {
        var first = columnXs[0];
        var last = columnXs[^1] + NodeWidth;
        foreach (var offset in new[] { 12, 30, 52, 72, 92, 112 })
        {
            yield return first - offset;
            yield return last + offset;
        }
        for (var i = 0; i < columnXs.Length - 1; i++)
        {
            var right = columnXs[i] + NodeWidth;
            for (var x = right + Stub; x < columnXs[i + 1]; x += LaneStep)
                yield return x;
        }
    }

    private static DiagramBox RelativeBox(DiagramBox box, Frame? frame) => frame is null ? box :
        box with { X = box.X - frame.Box.X, Y = box.Y - frame.Box.Y };

    private static XElement Cell(string id, string kind, string parent, string label, DiagramBox box, string style) =>
        new("mxCell", new XAttribute("id", id), new XAttribute("vertex", "1"), new XAttribute("parent", parent),
            new XAttribute("kind", kind), new XAttribute("value", WebUtility.HtmlEncode(label)), new XAttribute("style", style),
            new XElement("mxGeometry", new XAttribute("x", box.X), new XAttribute("y", box.Y),
                new XAttribute("width", box.Width), new XAttribute("height", box.Height), new XAttribute("as", "geometry")));

    private static XElement ComponentIcon(string parent) =>
        new("mxCell", new XAttribute("id", NetworkMapGenerator.Id("component-icon:" + parent)), new XAttribute("vertex", "1"),
            new XAttribute("parent", parent), new XAttribute("kind", "component-icon"), new XAttribute("connectable", "0"),
            new XAttribute("style", "shape=component;jettyWidth=8;jettyHeight=4;fillColor=#ffffff;strokeColor=" + LabelColor +
                ";part=1;pointerEvents=0;movable=0;resizable=0;rotatable=0;"),
            new XElement("mxGeometry", new XAttribute("x", "1"), new XAttribute("y", "0.5"), new XAttribute("width", "20"),
                new XAttribute("height", "20"), new XAttribute("relative", "1"), new XAttribute("as", "geometry"),
                new XElement("mxPoint", new XAttribute("x", "-27"), new XAttribute("y", "-10"), new XAttribute("as", "offset"))));

    private static XElement Edge(Route route, IReadOnlyList<DiagramPoint> path, string color)
    {
        var (wire, start, end) = route;
        var parent = CommonFrame(start.Parent, end.Parent);
        var points = path.Count == 0 ? null : new XElement("Array", new XAttribute("as", "points"), path.Select(p =>
            new XElement("mxPoint", new XAttribute("x", p.X - (parent?.Box.X ?? 0)), new XAttribute("y", p.Y - (parent?.Box.Y ?? 0)))));
        var style = FormattableString.Invariant($"noEdgeStyle=1;rounded=0;jumpStyle=arc;jumpSize=8;labelAutoRotate=1;html=0;endArrow=block;exitX={start.X};exitY={start.Y};entryX={end.X};entryY={end.Y};exitPerimeter=0;entryPerimeter=0;fontSize=10;labelBackgroundColor=#ffffff;strokeColor={color};");
        var label = string.Join("\n", new[] { wire.Link.TransportLabel, wire.Link.ConfigurationLabel }.Where(text => text.Length > 0));
        return new XElement("mxCell", new XAttribute("id", wire.Link.Id), new XAttribute("source", start.CellId),
            new XAttribute("target", end.CellId), new XAttribute("value", label), new XAttribute("sourcePath", wire.Link.Path),
            new XAttribute("edge", "1"), new XAttribute("parent", parent?.Id ?? "1"), new XAttribute("style", style),
            new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry"), points));
    }

    private static Frame? CommonFrame(Frame? first, Frame? second)
    {
        for (var left = first; left is not null; left = left.Parent)
            for (var right = second; right is not null; right = right.Parent)
                if (left == right) return left;
        return null;
    }
}
