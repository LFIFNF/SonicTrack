namespace SonicTrack.V61;

internal static class SpectrumStabilizer
{
    // Fast attack preserves transients; slower release suppresses small fluctuations.
    // This runs before recording a column, never on the scrolling history.
    public static void Apply(float[] input, float[] state, int rows, double elapsedSeconds, float stabilityMs)
    {
        double release = Math.Max(0, stabilityMs) / 1000.0;
        double elapsed = Math.Clamp(elapsedSeconds, 0, .25);
        for (int b = 0; b < rows; b++)
        {
            double tau = input[b] > state[b] ? release * .18 : release;
            float alpha = tau <= 0 ? 1f : (float)(1 - Math.Exp(-elapsed / tau));
            state[b] += (input[b] - state[b]) * alpha;
        }
    }
}
