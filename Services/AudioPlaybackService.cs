using System;
using NAudio.Wave;

namespace TS3ScreenShare.Services
{
    /// <summary>
    /// Plays incoming 16-bit PCM chunks through the default audio device.
    /// </summary>
    public sealed class AudioPlaybackService : IDisposable
    {
        private BufferedWaveProvider? _buffer;
        private WaveOutEvent? _waveOut;
        private bool _muted;

        public bool IsMuted
        {
            get => _muted;
            set
            {
                _muted = value;
                if (_waveOut != null)
                    _waveOut.Volume = value ? 0f : 1f;
            }
        }

        public void Initialize(int sampleRate, int channels)
        {
            Stop();
            var format = new WaveFormat(sampleRate, 16, channels);
            _buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(500)
            };
            _waveOut = new WaveOutEvent { DesiredLatency = 100 };
            _waveOut.Init(_buffer);
            _waveOut.Play();
        }

        public void AddSamples(byte[] data)
        {
            _buffer?.AddSamples(data, 0, data.Length);
        }

        public void Stop()
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _buffer = null;
        }

        public void Dispose() => Stop();
    }
}
