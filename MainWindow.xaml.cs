using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TS3ScreenShare.Models;
using TS3ScreenShare.Services;
using TS3ScreenShare.Shared;
using TS3ScreenShare.Views;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using MessageBox = System.Windows.MessageBox;

namespace TS3ScreenShare
{
    public partial class MainWindow : Window
    {
        private readonly TS3QueryService _ts3 = new();
        private readonly RelayService _relay = new();
        private readonly ScreenCaptureService _capture = new();
        private readonly AudioCaptureService _audioCapture = new();
        private readonly AudioPlaybackService _audioPlayback = new();
        private readonly SettingsService _settings = new();
        private readonly UpdateCheckService _updateChecker = new();
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private bool _exitRequested;
        private readonly ObservableCollection<SidebarItem> _sidebarItems = new();
        private readonly ObservableCollection<StreamInfo> _activeStreams = new();

        private bool _streaming;
        private string? _currentStreamId;
        private int _sendingFrame;

        private bool _viewing;
        private string? _viewingStreamId;

        private const string DefaultRelayUrl = "wss://0.0.0.0:5000";

        public MainWindow()
        {
            InitializeComponent();
            ChannelList.ItemsSource = _sidebarItems;
            StreamList.ItemsSource = _activeStreams;

            var saved = _settings.Load();
            var ts3ApiKey = TryReadTs3ApiKey();
            var apiKey = saved.ApiKey;

            if (!string.IsNullOrEmpty(ts3ApiKey) && ts3ApiKey != saved.ApiKey)
            {
                apiKey = ts3ApiKey;
                _settings.Save(new AppSettings { ApiKey = apiKey, RelayUrl = saved.RelayUrl });
            }

            PwdApiKey.Password = apiKey;
            TxtApiKey.Text = apiKey;
            if (!string.IsNullOrEmpty(saved.RelayUrl)) TxtRelayUrl.Text = saved.RelayUrl;

            _ts3.RosterUpdated += OnRosterUpdated;
            _ts3.Disconnected += OnTs3Disconnected;

            _relay.AudioFrameReceived += OnAudioFrameReceived;
            _relay.StreamAdded += OnStreamAdded;
            _relay.StreamRemoved += OnStreamRemoved;
            _relay.StreamsReset += OnStreamsReset;
            _relay.VideoFrameReceived += OnVideoFrameReceived;
            _relay.StreamDenied += OnStreamDenied;
            _relay.ConnectionDenied += OnConnectionDenied;
            _relay.Disconnected += OnRelayDisconnected;
            _relay.ForceDisconnected += OnRelayForceDisconnected;
            _relay.AuthChallengeReceived += OnAuthChallenge;
            _relay.Reconnecting += OnRelayReconnecting;
            _relay.Reconnected += OnRelayReconnected;

            _ts3.ChannelChanged += OnTs3ChannelChanged;

            _capture.FrameCaptured += OnFrameCaptured;
            _capture.CaptureFailed += OnCaptureFailed;
            _audioCapture.DataAvailable += OnAudioCaptured;

            App.PipeCommandReceived += OnPipeCommand;

            // Pre-fill relay URL if launched by the plugin with --relay arg
            if (!string.IsNullOrEmpty(App.StartupRelayUrl))
            {
                TxtRelayUrl.Text = App.StartupRelayUrl;
                Loaded += (_, _) => _ = AutoConnectAsync();
            }

            _ = CheckForUpdateAsync();
            InitializeTrayIcon();
        }

        private string ApiKeyValue =>
            TxtApiKey.Visibility == Visibility.Visible
                ? TxtApiKey.Text.Trim()
                : PwdApiKey.Password.Trim();

private void BtnToggleApiKey_Click(object sender, RoutedEventArgs e)
            => TogglePasswordVisibility(PwdApiKey, TxtApiKey, BtnToggleApiKey);

        private static void TogglePasswordVisibility(
            PasswordBox pwd, System.Windows.Controls.TextBox txt, System.Windows.Controls.Button btn)
        {
            if (pwd.Visibility == Visibility.Visible)
            {
                txt.Text = pwd.Password;
                pwd.Visibility = Visibility.Collapsed;
                txt.Visibility = Visibility.Visible;
                btn.Opacity = 1.0;
            }
            else
            {
                pwd.Password = txt.Text;
                txt.Visibility = Visibility.Collapsed;
                pwd.Visibility = Visibility.Visible;
                btn.Opacity = 0.5;
            }
        }

        private static string? TryReadTs3ApiKey()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TS3Client", "clientquery.ini");

            if (!File.Exists(path)) return null;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("api_key=", StringComparison.OrdinalIgnoreCase))
                    return trimmed["api_key=".Length..].Trim();
            }
            return null;
        }

        // ── Titlebar ──────────────────────────────────────────────────────────

        private void Titlebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else
                DragMove();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Hide();

        // ── Connection ────────────────────────────────────────────────────────

        private async Task AutoConnectAsync()
        {
            // Retry up to 5 times with increasing delays — TS3 ClientQuery may not be ready yet
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await Task.Delay(1500 + attempt * 1000);
                if (_ts3.IsConnected) return;
                try
                {
                    var apiKey = ApiKeyValue;
                    await _ts3.ConnectAsync(string.IsNullOrEmpty(apiKey) ? null : apiKey);
                    await _ts3.InitializeAsync();
                }
                catch { continue; }

                // TS3 connected — update UI and connect relay
                DotTS3.Fill = (Brush)FindResource("GreenBrush");
                ValTS3.Text = _ts3.MyUsername ?? "Connected";
                BtnConnect.IsEnabled = false;
                BtnConnect.Content = "Connecting...";
                try
                {
                    var relayUrl = TxtRelayUrl?.Text?.Trim();
                    if (string.IsNullOrEmpty(relayUrl)) relayUrl = DefaultRelayUrl;
                    await _relay.ConnectAsync(relayUrl);
                    await _relay.RequestAuthAsync();
                    var myChannelId = _ts3.MyChannelId ?? "0";
                    await _relay.JoinChannelAsync(myChannelId, GetCurrentChannelName(myChannelId));
                    DotRelay.Fill = (Brush)FindResource("GreenBrush");
                    ValRelay.Text = "Connected";
                    BtnStartStream.IsEnabled = true;
                }
                catch { /* relay unavailable — user can retry manually */ }
                _settings.Save(new Models.AppSettings
                {
                    ApiKey = ApiKeyValue,
                    RelayUrl = TxtRelayUrl?.Text?.Trim() ?? DefaultRelayUrl
                });
                BtnConnect.Content = "Disconnect";
                BtnConnect.IsEnabled = true;
                return;
            }
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_ts3.IsConnected)
            {
                await DisconnectAsync();
                return;
            }

            BtnConnect.IsEnabled = false;
            BtnConnect.Content = "Connecting...";

            try
            {
                var apiKey = ApiKeyValue;
                await _ts3.ConnectAsync(string.IsNullOrEmpty(apiKey) ? null : apiKey);
                await _ts3.InitializeAsync();
                DotTS3.Fill = (Brush)FindResource("GreenBrush");
                ValTS3.Text = _ts3.MyUsername ?? "Connected";
            }
            catch (Exception ex)
            {
                DotTS3.Fill = (Brush)FindResource("RedBrush");
                ValTS3.Text = "Error";
                BtnConnect.Content = "Connect";
                BtnConnect.IsEnabled = true;
                MessageBox.Show(
                    $"Failed to connect to TeamSpeak.\n\nIs the TS3 client running with the ClientQuery plugin?\n\n{ex.Message}",
                    "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var relayUrl = TxtRelayUrl?.Text?.Trim();
                if (string.IsNullOrEmpty(relayUrl)) relayUrl = DefaultRelayUrl;

                await _relay.ConnectAsync(relayUrl);

                // Identity verification via TS3 away message — waits for result
                // OnAuthChallenge handler sets away message and sends ConfirmAuth
                await _relay.RequestAuthAsync();

                var myChannelId = _ts3.MyChannelId ?? "0";
                await _relay.JoinChannelAsync(myChannelId, GetCurrentChannelName(myChannelId));
                DotRelay.Fill = (Brush)FindResource("GreenBrush");
                ValRelay.Text = "Connected";
                BtnStartStream.IsEnabled = true;
            }
            catch (Exception ex)
            {
                DotRelay.Fill = (Brush)FindResource("RedBrush");
                ValRelay.Text = "Error";
                MessageBox.Show(
                    $"TS3 connected, but the relay server is unavailable.\nStreaming will not work.\n\n{ex.Message}",
                    "Relay Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _settings.Save(new Models.AppSettings
            {
                ApiKey = ApiKeyValue,
                RelayUrl = TxtRelayUrl?.Text?.Trim() ?? DefaultRelayUrl
            });

            BtnConnect.Content = "Disconnect";
            BtnConnect.IsEnabled = true;
        }

        private async Task DisconnectAsync()
        {
            BtnConnect.IsEnabled = false;
            BtnConnect.Content = "Disconnecting...";

            try { if (_viewing) await StopViewingAsync(); } catch { }
            try { if (_streaming) await StopStreamAsync(); } catch { }
            try { await _relay.DisconnectAsync(); } catch { }
            _ts3.Disconnect();

            DotTS3.Fill = (Brush)FindResource("DimBrush");
            ValTS3.Text = "Disconnected";
            DotRelay.Fill = (Brush)FindResource("DimBrush");
            ValRelay.Text = "Disconnected";
            BtnStartStream.IsEnabled = false;
            _sidebarItems.Clear();
            _activeStreams.Clear();
            UpdateMainArea();

            BtnConnect.Content = "Connect";
            BtnConnect.IsEnabled = true;
        }

        // ── Streaming ─────────────────────────────────────────────────────────

        private async void BtnStartStream_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_viewing)
                    await StopViewingAsync();
                else if (_streaming)
                    await StopStreamAsync();
                else
                    await StartStreamAsync();
            }
            catch (Exception ex)
            {
                BtnStartStream.IsEnabled = true;
                MessageBox.Show($"Stream error:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StartStreamAsync()
        {
            BtnStartStream.IsEnabled = false;

            var dialog = new CaptureSourceDialog { Owner = this };
            if (dialog.ShowDialog() != true || dialog.SelectedSource == null)
            {
                BtnStartStream.IsEnabled = true;
                return;
            }
            var source = dialog.SelectedSource;

            _currentStreamId = Guid.NewGuid().ToString("N")[..8];
            var channelId = _ts3.MyChannelId ?? "0";
            var channelName = GetCurrentChannelName(channelId);
            var username = _ts3.MyUsername ?? "unknown";

            _audioCapture.Start();
            await _relay.RegisterStreamAsync(_currentStreamId, channelId, channelName, username,
                _audioCapture.SampleRate, _audioCapture.Channels);

            // Chat notification — sent only by the streamer, once
            var settings = _settings.Load();
            if (settings.NotificationChat && _ts3.IsConnected)
            {
                try { await _ts3.SendChannelMessageAsync($"[TS3SS] {username} started streaming"); }
                catch { }
            }

            _capture.Start(15, source);

            _streaming = true;
            BtnStartStream.Content = "■  Stop stream";
            BtnStartStream.IsEnabled = true;
            StreamStatusText.Text = $"  Streaming  [{_currentStreamId}]";
        }

        private async Task StopStreamAsync()
        {
            BtnStartStream.IsEnabled = false;

            _capture.Stop();
            _audioCapture.Stop();
            if (_currentStreamId != null)
                await _relay.StopStreamAsync(_currentStreamId);

            _streaming = false;
            _currentStreamId = null;
            BtnStartStream.Content = "▶  Start stream";
            BtnStartStream.IsEnabled = true;
            StreamStatusText.Text = "";
        }

        // ── Audio capture → PCM → Relay ──────────────────────────────────────

        private async Task CheckForUpdateAsync()
        {
            var update = await _updateChecker.CheckAsync();
            if (update is null) return;

            Dispatcher.Invoke(() =>
            {
                var result = MessageBox.Show(
                    $"A new version of TS3ScreenShare is available: v{update.LatestVersion}\n\n" +
                    $"You are running v{UpdateCheckService.CurrentVersion}.\n\n" +
                    "Do you want to open the download page?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(update.ReleaseUrl)
                        { UseShellExecute = true });
            });
        }

        private void OnCaptureFailed(Exception ex)
            => Dispatcher.Invoke(async () =>
            {
                try { await StopStreamAsync(); } catch { }
                MessageBox.Show($"Screen capture failed after repeated errors:\n\n{ex.Message}",
                    "Capture Error", MessageBoxButton.OK, MessageBoxImage.Error);
            });

        private void OnAudioCaptured(byte[] pcmData)
        {
            if (!_streaming || _currentStreamId == null) return;
            var streamId = _currentStreamId;
            _ = Task.Run(async () =>
            {
                try { await _relay.SendAudioFrameAsync(streamId, pcmData); }
                catch { }
            });
        }

        // ── Screen capture → JPEG → Relay ────────────────────────────────────

        private void OnFrameCaptured(int width, int height, byte[] bgrBytes)
        {
            if (!_streaming || _currentStreamId == null) return;
            if (Interlocked.CompareExchange(ref _sendingFrame, 1, 0) != 0) return; // skip frame if previous send is still in progress

            var streamId = _currentStreamId;
            _ = Task.Run(async () =>
            {
                try
                {
                    var jpeg = BgrToJpeg(bgrBytes, width, height);
                    await _relay.SendVideoFrameAsync(streamId, jpeg);
                }
                finally
                {
                    Interlocked.Exchange(ref _sendingFrame, 0);
                }
            });
        }

        private static byte[] BgrToJpeg(byte[] bgrBytes, int width, int height)
        {
            using var bmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = bmp.LockBits(
                new System.Drawing.Rectangle(0, 0, width, height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            Marshal.Copy(bgrBytes, 0, data.Scan0, bgrBytes.Length);
            bmp.UnlockBits(data);

            using var ms = new MemoryStream();
            var jpegCodec = System.Drawing.Imaging.ImageCodecInfo
                .GetImageEncoders()
                .First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
            encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, 60L);
            bmp.Save(ms, jpegCodec, encoderParams);
            return ms.ToArray();
        }

        // ── Audio přehrávání ──────────────────────────────────────────────────

        private void OnAudioFrameReceived(string streamId, byte[] pcmData)
        {
            if (!_viewing || streamId != _viewingStreamId) return;
            _audioPlayback.AddSamples(pcmData);
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            _audioPlayback.IsMuted = !_audioPlayback.IsMuted;
            BtnMute.Content = _audioPlayback.IsMuted ? "🔇" : "🔊";
        }

        // ── Video přehrávání ──────────────────────────────────────────────────

        private void OnVideoFrameReceived(string streamId, byte[] jpegBytes)
        {
            if (!_viewing || streamId != _viewingStreamId) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.StreamSource = new MemoryStream(jpegBytes);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze();
                    VideoDisplay.Source = bi;
                    VideoDisplay.Visibility = Visibility.Visible;
                    MainPlaceholder.Visibility = Visibility.Collapsed;
                    StreamsPanel.Visibility = Visibility.Collapsed;
                }
                catch { }
            });
        }

        // ── TS3 Roster ────────────────────────────────────────────────────────

        private void OnRosterUpdated(IReadOnlyList<TS3ChannelInfo> channels, IReadOnlyList<TS3ClientInfo> clients)
        {
            var items = BuildSidebarItems(channels, clients);
            Dispatcher.Invoke(() =>
            {
                _sidebarItems.Clear();
                foreach (var item in items) _sidebarItems.Add(item);
            });
        }

        private void OnTs3Disconnected()
        {
            Dispatcher.InvokeAsync(async () =>
            {
                try { if (_streaming) await StopStreamAsync(); } catch { }
                try { if (_viewing) await StopViewingAsync(); } catch { }
                try { await _relay.DisconnectAsync(); } catch { }

                DotTS3.Fill = (Brush)FindResource("DimBrush");
                ValTS3.Text = "Disconnected";
                DotRelay.Fill = (Brush)FindResource("DimBrush");
                ValRelay.Text = "Disconnected";
                BtnConnect.Content = "Connect";
                BtnConnect.IsEnabled = true;
                BtnStartStream.IsEnabled = false;
                _sidebarItems.Clear();
                _activeStreams.Clear();
            });
        }

        // ── Viewer ────────────────────────────────────────────────────────────

        private async void BtnWatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            var streamId = btn.Tag as string;
            if (string.IsNullOrEmpty(streamId)) return;

            try
            {
                btn.IsEnabled = false;
                _viewingStreamId = streamId;
                await _relay.WatchStreamAsync(streamId);
                _viewing = true;

                // Initialize audio playback if stream has audio
                var streamInfo = _activeStreams.FirstOrDefault(s => s.StreamId == streamId);
                if (streamInfo?.AudioSampleRate > 0)
                {
                    _audioPlayback.Initialize(streamInfo.AudioSampleRate, streamInfo.AudioChannels);
                    BtnMute.Visibility = Visibility.Visible;
                }

                StreamStatusText.Text = $"  Watching  [{streamId}]";
                BtnStartStream.Content = "■  Stop";
                BtnStartStream.IsEnabled = true;
                UpdateMainArea();
            }
            catch (Exception ex)
            {
                btn.IsEnabled = true;
                MessageBox.Show($"Failed to connect to stream:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task StopViewingAsync()
        {
            if (_viewingStreamId != null)
                await _relay.UnwatchStreamAsync(_viewingStreamId);

            _viewing = false;
            _viewingStreamId = null;
            VideoDisplay.Visibility = Visibility.Collapsed;
            VideoDisplay.Source = null;
            _audioPlayback.Stop();
            BtnMute.Visibility = Visibility.Collapsed;
            BtnMute.Content = "🔊";

            // Restore button state depending on whether we are still streaming
            if (_streaming)
            {
                BtnStartStream.Content = "■  Stop stream";
                StreamStatusText.Text = $"  Streaming  [{_currentStreamId}]";
            }
            else
            {
                BtnStartStream.Content = "▶  Start stream";
                StreamStatusText.Text = "";
            }

            UpdateMainArea();
            ReenableWatchButtons();
        }

        private void ReenableWatchButtons()
        {
            foreach (var item in StreamList.Items)
            {
                var container = StreamList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
                if (container == null) continue;
                var btn = FindVisualChild<System.Windows.Controls.Button>(container);
                if (btn != null) btn.IsEnabled = true;
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void UpdateMainArea()
        {
            if (_viewing)
            {
                StreamsPanel.Visibility = Visibility.Collapsed;
                MainPlaceholder.Visibility = Visibility.Collapsed;
            }
            else if (_activeStreams.Count > 0)
            {
                StreamsPanel.Visibility = Visibility.Visible;
                MainPlaceholder.Visibility = Visibility.Collapsed;
                VideoDisplay.Visibility = Visibility.Collapsed;
            }
            else
            {
                StreamsPanel.Visibility = Visibility.Collapsed;
                MainPlaceholder.Visibility = Visibility.Visible;
            }
        }

        // ── Relay eventy ─────────────────────────────────────────────────────

        private void OnStreamAdded(StreamInfo info)
            => Dispatcher.Invoke(() =>
            {
                _activeStreams.Add(info);
                UpdateMainArea();

                // Notify only when the streamer is in the same TS3 channel as us, and we are not the streamer
                if (_ts3.IsConnected && info.ChannelId == _ts3.MyChannelId
                    && info.StreamerUsername != _ts3.MyUsername)
                    _ = Task.Run(SignalPluginNotification);
            });

        private static void SignalPluginNotification()
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting("Local\\TS3ScreenShare_Notify");
                evt.Set();
            }
            catch { /* plugin not loaded or TS3 not running */ }
        }

        private void OnStreamRemoved(string streamId)
            => Dispatcher.InvokeAsync(async () =>
            {
                var item = _activeStreams.FirstOrDefault(s => s.StreamId == streamId);
                if (item is not null) _activeStreams.Remove(item);

                // If we were actively watching this stream, stop — streamer moved to another channel
                if (_viewing && _viewingStreamId == streamId)
                    await StopViewingAsync();
                else
                    UpdateMainArea();
            });

        private void OnRelayDisconnected()
            => Dispatcher.Invoke(() =>
            {
                DotRelay.Fill = (Brush)FindResource("DimBrush");
                ValRelay.Text = "Disconnected";
                BtnStartStream.IsEnabled = false;
            });

        private void OnRelayReconnecting()
            => Dispatcher.Invoke(() =>
            {
                DotRelay.Fill = (Brush)FindResource("DimBrush");
                ValRelay.Text = "Reconnecting...";
            });

        private void OnRelayReconnected()
        {
            // SignalR reconnect restores the transport but all server-side state is gone.
            // Re-run auth + JoinChannel, then restore any active stream or viewer session.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _relay.RequestAuthAsync();
                    var channelId = _ts3.MyChannelId ?? "0";
                    var channelName = Dispatcher.Invoke(() => GetCurrentChannelName(channelId));
                    await _relay.JoinChannelAsync(channelId, channelName);

                    // Re-register stream if one was active before the disconnect
                    if (_streaming && _currentStreamId != null)
                    {
                        var username = _ts3.MyUsername ?? "unknown";
                        await _relay.RegisterStreamAsync(_currentStreamId, channelId, channelName,
                            username, _audioCapture.SampleRate, _audioCapture.Channels);
                    }

                    // Re-subscribe to stream if viewer was active before the disconnect
                    if (_viewing && _viewingStreamId != null)
                        await _relay.WatchStreamAsync(_viewingStreamId);

                    Dispatcher.Invoke(() =>
                    {
                        DotRelay.Fill = (Brush)FindResource("GreenBrush");
                        ValRelay.Text = "Connected";
                        BtnStartStream.IsEnabled = true;
                    });
                }
                catch
                {
                    // Reconnect failed — reset local stream/viewer state so the UI
                    // doesn't show an active session the server no longer knows about.
                    Dispatcher.Invoke(() =>
                    {
                        if (_streaming)
                        {
                            _capture.Stop();
                            _audioCapture.Stop();
                            _streaming = false;
                            _currentStreamId = null;
                            BtnStartStream.Content = "▶  Start stream";
                            StreamStatusText.Text = "";
                        }

                        if (_viewing)
                        {
                            _viewing = false;
                            _viewingStreamId = null;
                            VideoDisplay.Visibility = Visibility.Collapsed;
                            VideoDisplay.Source = null;
                            _audioPlayback.Stop();
                            BtnMute.Visibility = Visibility.Collapsed;
                            BtnMute.Content = "🔊";
                            UpdateMainArea();
                        }

                        DotRelay.Fill = (Brush)FindResource("RedBrush");
                        ValRelay.Text = "Error";
                        BtnStartStream.IsEnabled = false;
                    });
                }
            });
        }

        private void OnRelayForceDisconnected()
            => Dispatcher.Invoke(async () =>
            {
                try { if (_streaming) await StopStreamAsync(); } catch { }
                try { if (_viewing) await StopViewingAsync(); } catch { }
                try { await _relay.DisconnectAsync(); } catch { }
                DotRelay.Fill = (Brush)FindResource("RedBrush");
                ValRelay.Text = "Disconnected";
                BtnStartStream.IsEnabled = false;
                BtnConnect.Content = "Connect";
                MessageBox.Show(
                    "You have been disconnected from the relay server because you left the TeamSpeak server.",
                    "Disconnected", MessageBoxButton.OK, MessageBoxImage.Warning);
            });

        private void OnStreamsReset(IReadOnlyList<StreamInfo> streams)
            => Dispatcher.Invoke(() =>
            {
                _activeStreams.Clear();
                foreach (var s in streams) _activeStreams.Add(s);
                UpdateMainArea();
            });

        private void OnConnectionDenied(string message)
            => Dispatcher.Invoke(async () =>
            {
                MessageBox.Show($"Connection to relay server denied:\n\n{message}",
                    "Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                await DisconnectAsync();
            });

        private void OnStreamDenied(string message)
            => Dispatcher.Invoke(() =>
            {
                _streaming = false;
                _currentStreamId = null;
                BtnStartStream.Content = "▶  Start stream";
                StreamStatusText.Text = "";
                BtnStartStream.IsEnabled = true;
                MessageBox.Show($"Server denied stream:\n\n{message}", "Access Denied",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });

        private async void OnAuthChallenge(string token)
        {
            // Called from SignalR thread — set TS3 away message with token and confirm
            try
            {
                await _ts3.SetAwayMessageAsync($"TS3SS-{token}");
                await _relay.ConfirmAuthAsync(token);
            }
            catch (Exception ex)
            {
                // If this fails, RequestAuthAsync will time out
                Dispatcher.Invoke(() =>
                    MessageBox.Show($"Identity verification error:\n\n{ex.Message}",
                        "Auth Error", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            finally
            {
                // Always reset away message (even on error)
                try { await _ts3.SetAwayMessageAsync(null); } catch { }
            }
        }

        private void OnTs3ChannelChanged(string oldId, string newId)
        {
            if (!_relay.IsConnected) return;
            var channelName = GetCurrentChannelName(newId);
            _ = Task.Run(async () =>
            {
                try { await _relay.JoinChannelAsync(newId, channelName); }
                catch { }
            });
        }

        // ── System tray ──────────────────────────────────────────────────────

        private void InitializeTrayIcon()
        {
            var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo.ico");
            var icon = File.Exists(iconPath)
                ? new System.Drawing.Icon(iconPath)
                : System.Drawing.SystemIcons.Application;

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open TS3ScreenShare", null, (_, _) => ShowFromTray());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "TS3ScreenShare",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exitRequested)
            {
                e.Cancel = true;
                Hide();
                _trayIcon?.ShowBalloonTip(2000, "TS3ScreenShare",
                    "Running in background. Double-click tray icon to open.",
                    System.Windows.Forms.ToolTipIcon.Info);
                return;
            }
            _trayIcon?.Dispose();
            base.OnClosing(e);
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _exitRequested = true;
            Application.Current.Shutdown();
        }

        // ── Plugin pipe commands ──────────────────────────────────────────────

        private void OnPipeCommand(string command)
        {
            // RELAY:<url> — pre-fill relay URL
            if (command.StartsWith("RELAY:", StringComparison.OrdinalIgnoreCase))
            {
                var url = command["RELAY:".Length..].Trim();
                if (!string.IsNullOrEmpty(url))
                    TxtRelayUrl.Text = url;
                ShowFromTray();
                if (!_ts3.IsConnected)
                    _ = AutoConnectAsync();
                return;
            }

            // START_STREAM — open capture dialog and start streaming
            if (command.Equals("START_STREAM", StringComparison.OrdinalIgnoreCase))
            {
                ShowFromTray();
                if (!_streaming)
                    BtnStartStream_Click(this, null!);
                return;
            }

            // STOP_STREAM — stop current stream
            if (command.Equals("STOP_STREAM", StringComparison.OrdinalIgnoreCase))
            {
                if (_streaming)
                    BtnStartStream_Click(this, null!);
                return;
            }

            // WATCH_USER:<nickname> — find stream by username and start watching
            if (command.StartsWith("WATCH_USER:", StringComparison.OrdinalIgnoreCase))
            {
                var username = command["WATCH_USER:".Length..].Trim();
                ShowFromTray();
                _ = WatchStreamByUsernameAsync(username);
                return;
            }

            // FOCUS — bring window to front
            if (command.Equals("FOCUS", StringComparison.OrdinalIgnoreCase))
            {
                ShowFromTray();
                return;
            }
        }

        private async Task WatchStreamByUsernameAsync(string username)
        {
            var stream = _activeStreams.FirstOrDefault(s =>
                s.StreamerUsername.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (stream == null)
            {
                MessageBox.Show($"No active stream found for user \"{username}\".",
                    "TS3ScreenShare", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                if (_viewing) await StopViewingAsync();
                _viewingStreamId = stream.StreamId;
                await _relay.WatchStreamAsync(stream.StreamId);
                _viewing = true;

                if (stream.AudioSampleRate > 0)
                {
                    _audioPlayback.Initialize(stream.AudioSampleRate, stream.AudioChannels);
                    BtnMute.Visibility = Visibility.Visible;
                }

                StreamStatusText.Text = $"  Watching  [{stream.StreamId}]";
                BtnStartStream.Content = "■  Stop";
                BtnStartStream.IsEnabled = true;
                UpdateMainArea();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect to stream:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Stream notifications ──────────────────────────────────────────────

        private static void PlayNotificationSound()
        {
            var soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "notification.mp3");
            if (!File.Exists(soundPath)) return;

            _ = Task.Run(() =>
            {
                try
                {
                    using var reader = new NAudio.Wave.MediaFoundationReader(soundPath);
                    using var output = new NAudio.Wave.WaveOutEvent();
                    output.Init(reader);
                    output.Play();
                    while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
                        Thread.Sleep(50);
                }
                catch { }
            });
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private string GetCurrentChannelName(string channelId)
        {
            var item = _sidebarItems.FirstOrDefault(i => i.IsChannel && i.Id == channelId);
            return item?.Name ?? channelId;
        }

        private static List<SidebarItem> BuildSidebarItems(
            IReadOnlyList<TS3ChannelInfo> channels,
            IReadOnlyList<TS3ClientInfo> clients)
        {
            var items = new List<SidebarItem>();

            var byParent = channels
                .GroupBy(c => c.ParentId)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Name).ToList());

            var byChannel = clients
                .GroupBy(c => c.ChannelId)
                .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Nickname).ToList());

            void Visit(string parentId, int depth)
            {
                if (!byParent.TryGetValue(parentId, out var children)) return;
                foreach (var ch in children)
                {
                    items.Add(new SidebarItem
                    {
                        IsChannel = true, Id = ch.Id, Name = ch.Name,
                        Margin = new Thickness(depth * 12, 1, 4, 1)
                    });
                    if (byChannel.TryGetValue(ch.Id, out var chClients))
                    {
                        foreach (var cl in chClients)
                            items.Add(new SidebarItem
                            {
                                IsChannel = false, Id = cl.Id, Name = cl.Nickname,
                                IsSelf = cl.IsSelf,
                                Margin = new Thickness(depth * 12 + 16, 1, 4, 1)
                            });
                    }
                    Visit(ch.Id, depth + 1);
                }
            }

            Visit("0", 0);
            return items;
        }
    }
}
