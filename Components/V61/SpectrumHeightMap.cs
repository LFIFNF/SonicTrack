namespace SonicTrack.V61;

/// <summary>Compress existing dB display heights without changing their color records.</summary>
internal static class SpectrumHeightMap
{
    internal static float Map(float height, float compression)
    {
        float level = float.IsFinite(height) ? Math.Clamp(height, 0f, 1f) : 0f;
        float strength = float.IsFinite(compression) ? Math.Clamp(compression, 0f, 4f) : 0f;
        // Reduction = strength * h² / (1 + strength * h). Both absolute and
        // proportional reduction increase with h; ordering and silence are preserved.
        return level / (1f + strength * level);
    }
}
