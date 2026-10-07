using System.Buffers.Binary;
using NAudio.Wave;

namespace SonicTrack.WaveCore;

internal static class AudioSampleDecoder
{
    public static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible extensible &&
        extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

    public static void Validate(WaveFormat format)
    {
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible extensible &&
            extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (!(IsFloat(format) && format.BitsPerSample == 32) &&
            !(pcm && format.BitsPerSample is 8 or 16 or 24 or 32))
            throw new NotSupportedException($"不支持的音频格式：{format}");
    }

    public static float Read(ReadOnlySpan<byte> bytes, int bits, bool floatingPoint)
    {
        float value;
        if (floatingPoint)
            value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        else
            value = bits switch
            {
                8 => (bytes[0] - 128) / 128f,
                16 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
                32 => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
                _ => 0f
            };
        return float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
    }
}


