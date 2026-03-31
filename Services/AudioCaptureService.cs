using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TS3ScreenShare.Services
{
    /// <summary>
    /// Captures system audio (loopback) and emits 16-bit PCM chunks.
    /// Output format: device sample rate, 16-bit, same channel count.
    /// </summary>
    public sealed class AudioCaptureService : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private bool _running;

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        public event Action<byte[]>? DataAvailable;

        public void Start()
        {
            _capture = new WasapiLoopbackCapture();
            SampleRate = _capture.WaveFormat.SampleRate;
            Channels = _capture.WaveFormat.Channels;

            _capture.DataAvailable += OnData;
            _running = true;
            _capture.StartRecording();
        }

        public void Stop()
        {
            _running = false;
            if (_capture != null)
            {
                _capture.StopRecording();
                _capture.DataAvailable -= OnData;
                _capture.Dispose();
                _capture = null;
            }
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (!_running || e.BytesRecorded == 0) return;
            // WASAPI loopback returns IEEE Float 32-bit — convert to 16-bit PCM
            var pcm16 = ConvertFloatTo16Bit(e.Buffer, e.BytesRecorded);
            DataAvailable?.Invoke(pcm16);
        }

        private static byte[] ConvertFloatTo16Bit(byte[] floatBuffer, int byteCount)
        {
            int sampleCount = byteCount / 4;
            var output = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(floatBuffer, i * 4);
                sample = Math.Clamp(sample, -1f, 1f);
                short pcm = (short)(sample * 32767f);
                output[i * 2]     = (byte)(pcm & 0xFF);
                output[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
            }
            return output;
        }

        public void Dispose() => Stop();
    }
}
