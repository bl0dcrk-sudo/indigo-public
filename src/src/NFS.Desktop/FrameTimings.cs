namespace NFS.Desktop;

/// <summary><c>--perf</c>: per-frame times of the window host (software render, texture upload <c>SetData</c>, whole present) collected over a window of
/// frames; <see cref="Report"/> gives mean / p95 / max per stage in ms. No MonoGame types: testable without a window.</summary>
internal sealed class FrameTimings
{
    public enum Stage { Render, Upload, Present, Frame }

    readonly List<double>[] _ms = [new(), new(), new(), new()];
    readonly double[] _current = new double[4];

    /// <summary>Adds <paramref name="ms"/> to the stage of the frame in progress (upload can be several <c>SetData</c> calls).</summary>
    public void Add(Stage s, double ms) => _current[(int)s] += ms;

    /// <summary>Closes the frame in progress.</summary>
    public void EndFrame()
    {
        for (int i = 0; i < 4; i++) { _ms[i].Add(_current[i]); _current[i] = 0; }
    }

    public int Frames => _ms[0].Count;

    /// <summary>One line per stage: "render mean 12.30 p95 15.10 max 20.00 ms"; the first <paramref name="skip"/> frames (warm-up, JIT) are left out.</summary>
    public string Report(int skip = 0)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"perf: {Math.Max(0, Frames - skip)} frames");
        foreach (Stage s in Enum.GetValues<Stage>())
        {
            var v = _ms[(int)s].Skip(skip).OrderBy(x => x).ToArray();
            if (v.Length == 0) continue;
            double mean = v.Average(), p95 = v[Math.Min(v.Length - 1, (int)Math.Ceiling(v.Length * 0.95) - 1)], max = v[^1];
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"; {s.ToString().ToLowerInvariant()} mean {mean:F2} p95 {p95:F2} max {max:F2} ms");
        }
        return sb.ToString();
    }

    public void Clear()
    {
        foreach (var l in _ms) l.Clear();
        Array.Clear(_current);
    }
}
