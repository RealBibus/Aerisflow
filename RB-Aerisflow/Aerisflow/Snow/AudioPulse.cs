using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace Aerisflow;

public sealed class AudioPulse : IDisposable
{
    private const int WindowSize = 1024;

    private readonly object _gate = new object();
    private readonly float[] _window = new float[WindowSize];
    private WasapiLoopbackCapture? _capture;
    private bool _running;
    private int _fill;
    private int _sampleRate = 48000;
    private int _channels = 2;
    private int _bytes = 4;
    private bool _float = true;
    private string _format = "none";
    private double _coeff = 0.98;
    private double _lp1;
    private double _lp2;
    private double _level;
    private double _pulse;
    private double _flux;
    private double _fast;
    private double _slow;
    private double _cooldown;
    private double _silence;
    private double _retry;
    private double _logAt;
    private long _beats;

    public double Sensitivity { get; set; } = 1.0;

    public double Level { get { lock (_gate) return _level; } }
    public double Pulse { get { lock (_gate) return _pulse; } }
    public double Flux { get { lock (_gate) return _flux; } }
    public bool Running { get { lock (_gate) return _running; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                var capture = new WasapiLoopbackCapture();
                var format = capture.WaveFormat;
                _sampleRate = Math.Max(8000, format.SampleRate);
                _channels = Math.Max(1, format.Channels);
                _bytes = Math.Max(1, format.BitsPerSample / 8);
                _float = format.BitsPerSample == 32 &&
                (format.Encoding == WaveFormatEncoding.IeeeFloat ||
                format.Encoding == WaveFormatEncoding.Extensible);
                _format = $"{format.Encoding} {format.BitsPerSample}bit {_channels}ch {_sampleRate}Hz float={_float}";
                _coeff = Math.Exp(-2.0 * Math.PI * 145.0 / _sampleRate);
                _lp1 = 0;
                _lp2 = 0;
                _fill = 0;
                _silence = 0;
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnStopped;
                _capture = capture;
                capture.StartRecording();
                _running = true;
            }
            catch (Exception ex)
            {
                _format = "failed: " + ex.Message;
                Release();
                _retry = 8.0;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            Log();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _retry = 0;
            Release();
            _level = 0;
            _pulse = 0;
            _flux = 0;
            _fast = 0;
            _slow = 0;
        }
    }

    private void Release()
    {
        _running = false;
        var capture = _capture;
        _capture = null;
        if (capture == null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
        }
        catch { }
        try { capture.Dispose(); } catch { }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            Release();
            _format = e.Exception != null ? "stopped: " + e.Exception.Message : "stopped";
            _retry = 4.0;
            Log();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_gate)
        {
            if (!_running) return;
            int stride = _channels * _bytes;
            if (stride <= 0) return;
            int frames = e.BytesRecorded / stride;
            _silence = 0;
            double keep = 1.0 - _coeff;

            for (int i = 0; i < frames; i++)
            {
                int offset = i * stride;
                double sum = 0;
                for (int c = 0; c < _channels; c++)
                {
                    int p = offset + c * _bytes;
                    if (p + _bytes > e.BytesRecorded) break;
                    if (_float) sum += BitConverter.ToSingle(e.Buffer, p);
                    else if (_bytes == 2) sum += BitConverter.ToInt16(e.Buffer, p) / 32768.0;
                    else sum += BitConverter.ToInt32(e.Buffer, p) / 2147483648.0;
                }
                double sample = sum / _channels;
                _lp1 += (sample - _lp1) * keep;
                _lp2 += (_lp1 - _lp2) * keep;
                _window[_fill++] = (float)_lp2;
                if (_fill == WindowSize)
                {
                    _fill = 0;
                    Analyze((double)WindowSize / _sampleRate);
                }
            }
        }
    }

    private void Analyze(double dt)
    {
        double sum = 0;
        for (int i = 0; i < WindowSize; i++)
        {
            double v = _window[i];
            sum += v * v;
        }
        double rms = Math.Sqrt(sum / WindowSize);
        double raw = Math.Clamp(rms * 9.0 * Math.Max(0.1, Sensitivity), 0, 1);

        _fast += (raw - _fast) * (1.0 - Math.Exp(-dt / 0.05));
        _slow += (raw - _slow) * (1.0 - Math.Exp(-dt / 0.55));
        _flux = Math.Clamp((_fast - _slow) * 5.0, 0, 1);

        double attack = 1.0 - Math.Exp(-dt / 0.05);
        double release = 1.0 - Math.Exp(-dt / 0.30);
        _level += (raw - _level) * (raw > _level ? attack : release);

        if (_cooldown <= 0 && raw > 0.05 && raw > _slow * 1.18 + 0.02)
        {
            double strength = Math.Clamp((raw - _slow) * 3.2, 0.15, 1.0);
            if (strength > _pulse) _pulse = strength;
            _cooldown = 0.11;
            _beats++;
        }
    }

    public void Tick(double dt)
    {
        if (dt <= 0) return;
        lock (_gate)
        {
            _pulse *= Math.Exp(-dt * 6.0);
            if (_pulse < 0.002) _pulse = 0;
            if (_cooldown > 0) _cooldown -= dt;

            _silence += dt;
            if (_silence > 0.5)
            {
                double decay = Math.Exp(-dt * 3.0);
                _level *= decay;
                _fast *= decay;
                _slow *= decay;
                _flux *= decay;
            }

            if (!_running && _retry > 0)
            {
                _retry -= dt;
                if (_retry <= 0)
                {
                    _retry = 0;
                    Start();
                }
            }

            _logAt -= dt;
            if (_logAt <= 0)
            {
                _logAt = 2.0;
                Log();
            }
        }
    }

    private void Log()
    {
        try
        {
            string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aerisflow");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "bass.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.Delete(path);
            File.AppendAllText(path,
            $"{DateTime.Now:HH:mm:ss} running={_running} level={_level:0.00} pulse={_pulse:0.00} flux={_flux:0.00} beats={_beats} fmt={_format}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose() => Stop();
}