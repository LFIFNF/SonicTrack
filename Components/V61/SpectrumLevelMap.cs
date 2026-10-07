namespace SonicTrack.V61;

/// <summary>Amplitude -> relative decibels -> color display level (independent of test height modes).</summary>
internal static class SpectrumLevelMap
{
    internal const float FloorDb = -72f;
    internal const float CeilingDb = -6f;
    internal static float AmplitudeToDecibels(float amplitude) =>
        20f * MathF.Log10(amplitude + 1e-12f);

    internal static float ToDisplayLevel(float amplitude, float contrast)
    {
        float db = AmplitudeToDecibels(amplitude);
        float level = Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0f, 1f);
        // Contrast changes the displayed dB range; 1 preserves equal dB steps.
        return contrast == 1f ? level : Math.Clamp((level - .5f) * contrast + .5f, 0f, 1f);
    }
}
