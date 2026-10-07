using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SonicTrack.WaveCore;

// Extracted from V1: WASAPI loopback -> mono ring buffer -> latest waveform snapshot.
internal sealed class AudioCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly float[] _ring = new float[131072];
    private int _write;
    private long _lastPacket;
    private long _packets;
    private long _version;
    private readonly float[] _decoded = new float[131072];
    public long PacketCount => Interlocked.Read(ref _packets);
    private WasapiCapture? _capture;
    private MMDevice? _device;
    public int SampleRate { get; private set; } = 48000;
    public string Description { get; private set; } = "未连接";
    public event Action<string>? Stopped;

    public void Start()
    {
        Dispose();
        Exception? lastError = null;
        foreach (int bufferMs in new[] { 10, 20, 100 })
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var capture = bufferMs == 100 ? new WasapiLoopbackCapture(_device)
                    : (WasapiCapture)new EventLoopback(_device, bufferMs);
                _capture = capture;
                AudioSampleDecoder.Validate(capture.WaveFormat);
                SampleRate = capture.WaveFormat.SampleRate;
                Description = $"{_device.FriendlyName} · {SampleRate} Hz · {capture.WaveFormat.Channels} 声道 · " +
                    (bufferMs == 100 ? "兼容捕获 100 ms" : $"事件捕获 {bufferMs} ms");
                capture.DataAvailable += OnData;
                capture.RecordingStopped += (_, e) =>
                {
                    if (ReferenceEquals(_capture, capture))
                        Stopped?.Invoke(e.Exception == null ? "音频捕获已停止" : "捕获异常：" + e.Exception.Message);
                };
                capture.StartRecording();
                return;
            }
            catch (Exception ex) { lastError = ex; Dispose(); }
        }
        throw new InvalidOperationException("低延迟和兼容音频捕获均无法启动", lastError);
    }
    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiCapture capture || !ReferenceEquals(capture, _capture)) return;
        if (e.BytesRecorded <= 0) return;
        var format = capture.WaveFormat;
        int bytes = format.BitsPerSample / 8, channels = format.Channels;
        int frame = format.BlockAlign;
        bool isFloat = AudioSampleDecoder.IsFloat(format);
        int frames = Math.Min(e.BytesRecorded / frame, _decoded.Length);
        // Decode on the audio thread before the short ring-copy lock, so UI snapshots don't wait on decoding.
        for (int f = 0; f < frames; f++)
        {
            float sample = 0;
            for (int c = 0; c < channels; c++)
                sample += AudioSampleDecoder.Read(e.Buffer.AsSpan(f * frame + c * bytes, bytes), format.BitsPerSample, isFloat);
            _decoded[f] = sample / channels;
        }
        lock (_gate)
        {
            int first = Math.Min(frames, _ring.Length - _write);
            Array.Copy(_decoded, 0, _ring, _write, first);
            if (first < frames) Array.Copy(_decoded, first, _ring, 0, frames - first);
            _write = (_write + frames) % _ring.Length;
            _lastPacket = Stopwatch.GetTimestamp();
            _version++;
            Interlocked.Increment(ref _packets);
        }
    }
    public int Snapshot(float[] target, double durationMs) => Snapshot(target, durationMs, out _);
    public int Snapshot(float[] target, double durationMs, out long version)
    {
        int count = Math.Clamp((int)(SampleRate * durationMs / 1000), 2, Math.Min(_ring.Length, target.Length));
        lock (_gate)
        {
            if (_lastPacket == 0 || Stopwatch.GetElapsedTime(_lastPacket).TotalSeconds > .25)
            {
                Array.Clear(target, 0, count);
                version = -_version - 1;
            }
            else
            {
                version = _version;
                int start = (_write - count + _ring.Length) % _ring.Length;
                int first = Math.Min(count, _ring.Length - start);
                Array.Copy(_ring, start, target, 0, first);
                if (first < count) Array.Copy(_ring, 0, target, first, count - first);
            }
        }
        return count;
    }
    public void Dispose()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnData;
            try { capture.StopRecording(); } finally { capture.Dispose(); }
        }
        _device?.Dispose(); _device = null;
        lock (_gate) { Array.Clear(_ring); _write = 0; _lastPacket = 0; _packets = 0; _version++; }
    }

    private sealed class EventLoopback : WasapiCapture
    {
        public EventLoopback(MMDevice device, int bufferMs) : base(device, true, bufferMs) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags()
            => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }
}

