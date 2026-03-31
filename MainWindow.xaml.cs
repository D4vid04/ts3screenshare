using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            var apiKey = !string.IsNullOrEmpty(saved.ApiKey)
                ? saved.ApiKey
                : TryReadTs3ApiKey() ?? "";
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

            _ts3.ChannelChanged += OnTs3ChannelChanged;

            _capture.FrameCaptured += OnFrameCaptured;
            _audioCapture.DataAvailable += OnAudioCaptured;
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
            => Application.Current.Shutdown();

        // ── Connection ────────────────────────────────────────────────────────

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

            _capture.Start(15, source);

            _streaming = true;
            BtnStartStream.Content = "■  Stop stream";
            StreamStatusText.Text = $"  Streaming  [{_currentStreamId}]";
            BtnStartStream.IsEnabled = true;
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
            StreamStatusText.Text = "";
            BtnStartStream.IsEnabled = true;
        }

        // ── Audio capture → PCM → Relay ──────────────────────────────────────

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
            Dispatcher.Invoke(() =>
            {
                DotTS3.Fill = (Brush)FindResource("DimBrush");
                ValTS3.Text = "Disconnected";
                BtnConnect.Content = "Connect";
                BtnConnect.IsEnabled = true;
                BtnStartStream.IsEnabled = false;
                _sidebarItems.Clear();
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
            => Dispatcher.Invoke(() => { _activeStreams.Add(info); UpdateMainArea(); });

        private void OnStreamRemoved(string streamId)
            => Dispatcher.Invoke(() =>
            {
                var item = _activeStreams.FirstOrDefault(s => s.StreamId == streamId);
                if (item is not null) _activeStreams.Remove(item);
                UpdateMainArea();
            });

        private void OnRelayDisconnected()
            => Dispatcher.Invoke(() =>
            {
                DotRelay.Fill = (Brush)FindResource("DimBrush");
                ValRelay.Text = "Disconnected";
                BtnStartStream.IsEnabled = false;
            });

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
