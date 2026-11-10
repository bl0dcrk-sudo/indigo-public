namespace NFS.Desktop;

/// <summary>
/// Fly-through state: camera position as a fractional slice index moving along the track's COL slices,
/// speed control and track switching among the available tracks. Pure logic (testable without a window).
/// </summary>
internal sealed class FlyThrough
{
    public const float MaxSpeed = 300f, SpeedStep = 10f;

    readonly int[] _tracks;
    public int SliceCount { get; private set; }
    /// <summary>Fractional slice position, always in [0, SliceCount).</summary>
    public double Position { get; private set; }
    public float Speed { get; private set; }
    public bool Paused { get; set; }
    public int TrackIndex { get; private set; }
    public int Track => _tracks[TrackIndex];
    public IReadOnlyList<int> Tracks => _tracks;

    public FlyThrough(IReadOnlyList<int> tracks, int track, int sliceCount, float speed = 30f, int startSlice = 0)
    {
        if (tracks.Count == 0) throw new ArgumentException("no tracks", nameof(tracks));
        _tracks = [.. tracks];
        int ti = Array.IndexOf(_tracks, track);
        TrackIndex = ti < 0 ? 0 : ti;
        Speed = Math.Clamp(speed, -MaxSpeed, MaxSpeed);
        SetSliceCount(sliceCount, startSlice);
    }

    public void SetSliceCount(int sliceCount, int startSlice = 0)
    {
        if (sliceCount <= 0) throw new ArgumentOutOfRangeException(nameof(sliceCount));
        SliceCount = sliceCount;
        Position = Wrap(startSlice, sliceCount);
    }

    public void Update(double seconds)
    {
        if (!Paused) Position = Wrap(Position + Speed * seconds, SliceCount);
    }

    public void Faster() => Speed = Math.Min(MaxSpeed, Speed + SpeedStep);
    public void Slower() => Speed = Math.Max(-MaxSpeed, Speed - SpeedStep);

    /// <summary>Select next (+1) / previous (-1) track; returns the new track number. Caller reloads and calls SetSliceCount.</summary>
    public int ChangeTrack(int delta)
    {
        TrackIndex = (int)Wrap(TrackIndex + delta, _tracks.Length);
        return Track;
    }

    /// <summary>Slice the camera sits on and the interpolation fraction to the next one.</summary>
    public (int Slice, int Next, float Frac) CameraSlice()
    {
        int s = (int)Math.Floor(Position);
        if (s >= SliceCount) s = 0;
        return (s, (s + 1) % SliceCount, (float)(Position - s));
    }

    static double Wrap(double v, int n)
    {
        double r = v % n;
        if (r < 0) r += n;
        return r >= n ? 0 : r;
    }

    /// <summary>Track numbers with a TRK file under GAMEDATA/TRACKS/SE (e.g. 0, 2..8 in NFS2SE).</summary>
    public static int[] AvailableTracks(string? gameDir)
    {
        var list = new List<int>();
        for (int t = 0; t < 100; t++)
            if (NFS.Data.GameFiles.Exists($"GAMEDATA/TRACKS/SE/TR{t:00}.TRK", gameDir)) list.Add(t);
        return [.. list];
    }
}
