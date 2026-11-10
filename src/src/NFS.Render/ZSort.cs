namespace NFS.Render;

/// <summary>
/// depth sort: 1240 buckets of 0.5 m. bucket ((z0+z1+z2+z3)/4 − (bias − 0xA0000)) >> 15 (z in 16.16); [ref:2723]
/// road polygons use bias 0 (= +10 m behind cars), the second LOD group 0x60000 (= +4 m), cars 0xA0000. Insert = push to the bucket head (LIFO);
/// yields far → near. Enhanced "Extended" view range uses more buckets of the same size (<see cref="ExtendedBuckets"/>); [ref:2724]
/// a polygon beyond the last bucket is not drawn (it is counted in <see cref="FarDropped"/>).
/// </summary>
public sealed class ZSort<T>
{
    public const int Buckets = 1240;
    /// <summary>Buckets of the Extended view range: 2480 × 0.5 m = 1240 m.</summary>
    public const int ExtendedBuckets = Buckets * 2;
    public const int WorldBias = 0, SceneryBias = 0x60000, CarBias = 0xA0000, ParticleBias = 0x88000, SkidBias = 0x60000;

    readonly List<T>[] _b;

    public ZSort(int buckets = Buckets)
    {
        Capacity = buckets;
        _b = Enumerable.Range(0, buckets).Select(_ => new List<T>()).ToArray();
    }

    /// <summary>Number of buckets of this sort (× 0.5 m).</summary>
    public int Capacity { get; }
    public int Count { get; private set; }
    /// <summary>Inserts refused since the last <see cref="Clear"/> because the polygon lies beyond the last bucket (too far) – polygons that would vanish.</summary>
    public int FarDropped { get; private set; }

    /// <summary>Bucket for an average view z (metres) and bias (16.16); −1 when out of [0, 1239] (or [0, <paramref name="buckets"/>−1]).</summary>
    public static int BucketFor(double avgZMetres, int bias = WorldBias, int buckets = Buckets)
    {
        long z = (long)Math.Floor(avgZMetres * 65536);
        long k = (z - (bias - 0xA0000)) >> 15;
        return k < 0 || k >= buckets ? -1 : (int)k;
    }

    public bool Insert(double avgZMetres, T item, int bias = WorldBias)
    {
        int k = BucketFor(avgZMetres, bias, Capacity);
        if (k < 0)
        {
            if (avgZMetres > 0) FarDropped++;
            return false;
        }
        _b[k].Add(item);
        Count++;
        return true;
    }

    public void Clear() { foreach (var l in _b) l.Clear(); Count = 0; FarDropped = 0; }

    /// <summary>Items of bucket <paramref name="k"/> in insertion order (allocation free access for the render loop: far → near = k descending, index descending).</summary>
    public int BucketCount(int k) => _b[k].Count;
    public T At(int k, int i) => _b[k][i];

    /// <summary>Far → near; inside a bucket last inserted first (LIFO list head).</summary>
    public IEnumerable<(int Bucket, T Item)> FarToNear()
    {
        for (int k = Capacity - 1; k >= 0; k--)
            for (int i = _b[k].Count - 1; i >= 0; i--) yield return (k, _b[k][i]);
    }
}
