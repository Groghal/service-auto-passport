namespace ServiceAutoPassport.Core;

/// <summary>Routes on a shared orthogonal grid, preferring free segments and fewer bends.</summary>
internal sealed class NetworkDiagramRouter
{
    private readonly double[] _xs;
    private readonly double[] _ys;
    private readonly int _width;
    private readonly bool[] _horizontal;
    private readonly bool[] _vertical;
    private readonly int[] _horizontalUses;
    private readonly int[] _verticalUses;
    private readonly byte[] _directionsUsed;
    private readonly record struct Segment(int From, int To, bool Vertical)
    {
        public int Index => Math.Min(From, To);
    }

    public NetworkDiagramRouter(IEnumerable<double> x, IEnumerable<double> y, DiagramBox[] obstacles)
    {
        _xs = x.Distinct().Order().ToArray();
        _ys = y.Distinct().Order().ToArray();
        _width = _xs.Length;
        var gridSize = _width * _ys.Length;
        _horizontal = new bool[gridSize];
        _vertical = new bool[gridSize];
        _horizontalUses = new int[gridSize];
        _verticalUses = new int[gridSize];
        _directionsUsed = new byte[gridSize];

        for (var row = 0; row < _ys.Length; row++)
        {
            var atHeight = obstacles.Where(box => box.Y < _ys[row] && _ys[row] < box.Bottom).ToArray();
            var below = row + 1 == _ys.Length ? Array.Empty<DiagramBox>() : obstacles
                .Where(box => box.Y < _ys[row + 1] && _ys[row] < box.Bottom).ToArray();
            for (var col = 0; col < _width; col++)
            {
                var index = row * _width + col;
                var px = _xs[col];
                _horizontal[index] = col + 1 < _width && !atHeight.Any(box => box.X < _xs[col + 1] && px < box.Right);
                _vertical[index] = row + 1 < _ys.Length && !below.Any(box => box.X < px && px < box.Right);
            }
        }
    }

    public DiagramPoint[]? TryRouteViaLane(DiagramPoint start, DiagramPoint end, double laneX)
    {
        var entry = new DiagramPoint(laneX, start.Y);
        var exit = new DiagramPoint(laneX, end.Y);
        var steps = Segments(Index(start), Index(entry), vertical: false)
            .Concat(Segments(Index(entry), Index(exit), vertical: true))
            .Concat(Segments(Index(exit), Index(end), vertical: false)).ToArray();
        foreach (var step in steps)
        {
            var clear = step.Vertical ? _vertical[step.Index] && _verticalUses[step.Index] == 0
                : _horizontal[step.Index] && _horizontalUses[step.Index] == 0;
            if (!clear) return null;
        }

        foreach (var step in steps) RecordUse(step);
        return [entry, exit];
    }

    private IEnumerable<Segment> Segments(int from, int to, bool vertical)
    {
        var stride = Math.Sign(to - from) * (vertical ? _width : 1);
        for (var index = from; index != to; index += stride)
            yield return new Segment(index, index + stride, vertical);
    }

    public DiagramPoint[] Route(DiagramPoint start, DiagramPoint end)
    {
        var from = Index(start);
        var to = Index(end);
        // Two states per point separate horizontal arrival from vertical arrival for bend costs.
        var cost = new double[_horizontal.Length * 2];
        Array.Fill(cost, double.PositiveInfinity);
        var previous = new int[cost.Length];
        Array.Fill(previous, -1);
        var queue = new PriorityQueue<int, (double Cost, int State)>();
        for (var axis = 0; axis < 2; axis++)
        {
            var state = from * 2 + axis;
            cost[state] = 0;
            queue.Enqueue(state, (0, state));
        }
        while (queue.TryDequeue(out var state, out var priority))
        {
            if (priority.Cost != cost[state]) continue;
            var point = state / 2;
            if (point == to) return Trace(state, previous);

            var col = point % _width;
            var row = point / _width;
            if (_horizontal[point]) Move(point + 1, 0, _xs[col + 1] - _xs[col], _horizontalUses[point]);
            if (col > 0 && _horizontal[point - 1]) Move(point - 1, 0, _xs[col] - _xs[col - 1], _horizontalUses[point - 1]);
            if (_vertical[point]) Move(point + _width, 1, _ys[row + 1] - _ys[row], _verticalUses[point]);
            if (row > 0 && _vertical[point - _width]) Move(point - _width, 1, _ys[row] - _ys[row - 1], _verticalUses[point - _width]);

            void Move(int next, int axis, double length, int uses)
            {
                var candidate = next * 2 + axis;
                var bend = state % 2 != axis ? 28 : 0;
                var crossesWire = (_directionsUsed[next] & (1 << (1 - axis))) != 0 && next != to ? 28 : 0;
                var score = cost[state] + length + bend + crossesWire + uses * length * 5;
                if (score >= cost[candidate]) return;
                cost[candidate] = score;
                previous[candidate] = state;
                queue.Enqueue(candidate, (score, candidate));
            }
        }
        throw new InvalidDataException("Не удалось проложить стрелку на сетевой схеме.");
    }

    private int Index(DiagramPoint p) => Array.BinarySearch(_ys, p.Y) * _width + Array.BinarySearch(_xs, p.X);
    private DiagramPoint Point(int index) => new(_xs[index % _width], _ys[index / _width]);

    private DiagramPoint[] Trace(int state, int[] previous)
    {
        var shape = new List<DiagramPoint> { Point(state / 2) };
        while (previous[state] != -1)
        {
            var last = previous[state];
            var from = last / 2;
            var to = state / 2;
            RecordUse(new Segment(from, to, state % 2 == 1));
            if (previous[last] == -1 || last % 2 != state % 2) shape.Add(Point(from));
            state = last;
        }
        shape.Reverse();
        return shape.ToArray();
    }

    private void RecordUse(Segment segment)
    {
        var uses = segment.Vertical ? _verticalUses : _horizontalUses;
        uses[segment.Index]++;
        var direction = (byte)(segment.Vertical ? 2 : 1);
        _directionsUsed[segment.From] |= direction;
        _directionsUsed[segment.To] |= direction;
    }
}
