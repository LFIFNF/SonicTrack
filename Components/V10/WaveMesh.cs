namespace SonicTrack.WaveCore;

/// <summary>Peak-preserving sample reduction and a single opaque triangle batch. Buffers grow only on resize.</summary>
internal sealed class WaveMesh
{
    private float[] _points = Array.Empty<float>();
    internal float[] Vertices { get; private set; } = Array.Empty<float>();
    internal int VertexCount { get; private set; }
    internal int PointCount { get; private set; }
    internal ReadOnlySpan<float> Points => _points.AsSpan(0, PointCount * 2);

    internal void Build(float[] samples, int count, int width, int height, double gain, double thickness)
    {
        if (count < 2 || count > samples.Length || width < 2 || height < 2)
        { VertexCount = PointCount = 0; return; }
        int buckets = Math.Min(count, Math.Clamp(width, 32, 4096));
        int maxPoints = buckets * 2;
        if (_points.Length < maxPoints * 2) _points = new float[maxPoints * 2];
        if (Vertices.Length < (maxPoints - 1) * 18) Vertices = new float[(maxPoints - 1) * 18];
        PointCount = VertexCount = 0;
        double inset = thickness / 2 + 2, usable = Math.Max(1, width - 2 * inset);
        double amplitude = Math.Max(0, (height - 2 * inset) * .45);
        void Add(int i)
        {
            double value = float.IsFinite(samples[i]) ? samples[i] : 0;
            _points[PointCount * 2] = (float)(inset + i * usable / (count - 1));
            _points[PointCount * 2 + 1] = (float)(height / 2.0 - Math.Clamp(value * gain, -1, 1) * amplitude);
            PointCount++;
        }
        for (int b = 0; b < buckets; b++)
        {
            int start = b * count / buckets, end = (b + 1) * count / buckets;
            int low = start, high = start;
            for (int i = start + 1; i < end; i++)
            {
                if (samples[i] < samples[low]) low = i;
                if (samples[i] > samples[high]) high = i;
            }
            if (low <= high) { Add(low); if (low != high) Add(high); }
            else { Add(high); Add(low); }
        }
        // Expand segments into quads and close outer join wedges with one bevel triangle.
        // Opaque overlaps do not require blending. The work is bounded by physical pixel columns.
        float half = (float)thickness / 2, previousNx = 0, previousNy = 0;
        void Vertex(float x, float y)
        { Vertices[VertexCount * 2] = x; Vertices[VertexCount * 2 + 1] = y; VertexCount++; }
        for (int i = 1; i < PointCount; i++)
        {
            float ax = _points[(i - 1) * 2], ay = _points[(i - 1) * 2 + 1];
            float bx = _points[i * 2], by = _points[i * 2 + 1];
            float dx = bx - ax, dy = by - ay, length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < .0001f) continue;
            float nx = -dy / length * half, ny = dx / length * half;
            Vertex(ax + nx, ay + ny); Vertex(ax - nx, ay - ny); Vertex(bx + nx, by + ny);
            Vertex(bx + nx, by + ny); Vertex(ax - nx, ay - ny); Vertex(bx - nx, by - ny);
            if (i > 1)
            {
                float side = previousNx * ny - previousNy * nx > 0 ? -1 : 1;
                Vertex(ax, ay); Vertex(ax + previousNx * side, ay + previousNy * side); Vertex(ax + nx * side, ay + ny * side);
            }
            previousNx = nx; previousNy = ny;
        }
    }
}
