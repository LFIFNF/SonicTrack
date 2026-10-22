using System.Diagnostics;
using System.Numerics;
using NAudio.Wave;

namespace SonicTrack.VintageAmpMeters;

internal sealed class AudioMeterAnalyzer : IDisposable
{
    private const int FftSize = 4096;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[65536];
    private readonly float[] _decoded = new float[65536];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private int _write;
    private long _lastPacket;
    private int _sampleRate = 48000;
    private WasapiLoopbackCapture? _capture;

    internal AudioMeterAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
    }

    internal int SampleRate => Volatile.Read(ref _sampleRate);
    internal string Description { get; private set; } = "未连接";
    internal event Action<string>? Stopped;

    internal void Start()
    {
        Dispose();
        var capture = new WasapiLoopbackCapture();
        try
        {
            ValidateFormat(capture.WaveFormat);
            Volatile.Write(ref _sampleRate, capture.WaveFormat.SampleRate);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            capture.StartRecording();
            Description = $"{capture.WaveFormat.SampleRate} Hz · {capture.WaveFormat.Channels} 声道";
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
            _capture = null;
            throw;
        }
    }

    internal bool ReadLevels(float[] destination)
    {
        if (destination.Length < 3)
            throw new ArgumentException("目标至少包含低、中、高三个电平。", nameof(destination));

        lock (_gate)
        {
            if (_lastPacket == 0 || Stopwatch.GetElapsedTime(_lastPacket).TotalSeconds > 0.25)
            {
                Array.Clear(destination, 0, 3);
                return false;
            }

            int start = (_write - FftSize + _ring.Length) % _ring.Length;
            for (int i = 0; i < FftSize; i++)
            {
                int index = (start + i) % _ring.Length;
                _fft[i] = new Complex(_ring[index] * _window[i], 0);
            }
        }

        Transform(_fft);
        double lowPower = 0, midPower = 0, highPower = 0;
        double nyquist = SampleRate * 0.5;
        for (int bin = 1; bin < FftSize / 2; bin++)
        {
            double frequency = (double)bin * SampleRate / FftSize;
            double amplitude = Complex.Abs(_fft[bin]) * (4.0 / FftSize);
            double power = amplitude * amplitude * 0.5;
            if (frequency >= 20 && frequency < 250)
                lowPower += power;
            else if (frequency >= 250 && frequency < 2000)
                midPower += power;
            else if (frequency >= 2000 && frequency <= Math.Min(20000, nyquist))
                highPower += power;
        }

        destination[0] = ToDecibels(lowPower);
        destination[1] = ToDecibels(midPower) + 3;
        destination[2] = ToDecibels(highPower) + 6;
        return true;
    }

    private static float ToDecibels(double power) =>
        power <= 1e-12 ? -120 : (float)(10 * Math.Log10(power));

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || !ReferenceEquals(capture, _capture))
            return;
        int bytesPerSample = capture.WaveFormat.BitsPerSample / 8;
        int channels = capture.WaveFormat.Channels;
        int frameSize = capture.WaveFormat.BlockAlign;
        if (bytesPerSample <= 0 || channels <= 0 || frameSize <= 0)
            return;

        int frames = Math.Min(e.BytesRecorded / frameSize, _decoded.Length);
        bool floatingPoint = IsFloat(capture.WaveFormat);
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (int channel = 0; channel < channels; channel++)
            {
                int offset = frame * frameSize + channel * bytesPerSample;
                sum += ReadSample(e.Buffer.AsSpan(offset, bytesPerSample),
                    capture.WaveFormat.BitsPerSample, floatingPoint);
            }
            _decoded[frame] = (float)(sum / channels);
        }

        lock (_gate)
        {
            for (int i = 0; i < frames; i++)
            {
                _ring[_write] = _decoded[i];
                _write = (_write + 1) % _ring.Length;
            }
            _lastPacket = Stopwatch.GetTimestamp();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is WasapiLoopbackCapture capture && ReferenceEquals(capture, _capture))
            Stopped?.Invoke(e.Exception == null ? "系统音频捕获已停止" : $"捕获异常：{e.Exception.Message}");
    }

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible extensible &&
        extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

    private static void ValidateFormat(WaveFormat format)
    {
        bool pcm = format.Encoding == WaveFormatEncoding.Pcm ||
            format is WaveFormatExtensible extensible &&
            extensible.SubFormat == new Guid("00000001-0000-0010-8000-00aa00389b71");
        if (!(IsFloat(format) && format.BitsPerSample == 32) &&
            !(pcm && format.BitsPerSample is 8 or 16 or 24 or 32))
            throw new NotSupportedException($"不支持的音频格式：{format}");
    }

    private static float ReadSample(ReadOnlySpan<byte> bytes, int bits, bool floatingPoint)
    {
        float value;
        if (floatingPoint)
            value = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes));
        else
            value = bits switch
            {
                8 => (bytes[0] - 128) / 128f,
                16 => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
                24 => ((bytes[0] | bytes[1] << 8 | bytes[2] << 16) << 8 >> 8) / 8388608f,
                32 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f,
                _ => 0f
            };
        return float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
    }

    private static void Transform(Complex[] values)
    {
        int length = values.Length;
        for (int i = 1, j = 0; i < length; i++)
        {
            int bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (values[i], values[j]) = (values[j], values[i]);
        }

        for (int size = 2; size <= length; size <<= 1)
        {
            Complex root = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (int offset = 0; offset < length; offset += size)
            {
                Complex factor = Complex.One;
                int half = size >> 1;
                for (int i = 0; i < half; i++)
                {
                    Complex even = values[offset + i];
                    Complex odd = factor * values[offset + i + half];
                    values[offset + i] = even + odd;
                    values[offset + i + half] = even - odd;
                    factor *= root;
                }
            }
        }
    }

    public void Dispose()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); }
            finally { capture.Dispose(); }
        }
        lock (_gate)
        {
            Array.Clear(_ring);
            _write = 0;
            _lastPacket = 0;
        }
    }
}
