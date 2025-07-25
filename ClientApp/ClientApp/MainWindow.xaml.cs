using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Net.Sockets;
using System.IO;
using Microsoft.Win32;
using System.Diagnostics;

namespace ClientApp
{
    
    public partial class MainWindow : Window
    {
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private Thread? _heartbeatThread;
        private bool _isConnected = false;
        private bool _heartbeatActive = false;
        private string _selectedVideoPath = string.Empty;
        private LibVLCSharp.Shared.LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private System.Windows.Controls.Image? _videoImage;
        private Process? _vlcProcess;

        private enum MessageType : byte
        {
            Heartbeat = 1,
            Coordinate = 2
        }

        public MainWindow()
        {
            InitializeComponent();
            ConnectButton.Click += ConnectButton_Click;
            SelectVideoButton.Click += SelectVideoButton_Click;
            StartVideoButton.Click += StartVideoButton_Click;
            SelectOnMapButton.Click += SelectOnMapButton_Click;
            // Video kontrollerini başta pasif yap
            StartVideoButton.IsEnabled = false;
            SelectVideoButton.IsEnabled = false;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected) return;
            string ip = IpTextBox.Text.Trim();
            int port = int.Parse(PortTextBox.Text.Trim());
            try
            {
                _tcpClient = new TcpClient();
                _tcpClient.Connect(ip, port);
                _stream = _tcpClient.GetStream();
                _isConnected = true;
                ConnectionStatusText.Text = "Durum: Bağlı";
                ConnectionStatusText.Foreground = Brushes.Green;
                _heartbeatActive = true;
                _heartbeatThread = new Thread(SendHeartbeatLoop) { IsBackground = true };
                _heartbeatThread.Start();
                // Bağlantı başarılı olunca video kontrollerini aktif et
                StartVideoButton.IsEnabled = true;
                SelectVideoButton.IsEnabled = true;
                ConnectButton.Visibility = Visibility.Collapsed; // ← yeni satır
                DisconnectButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                ConnectionStatusText.Text = $"Durum: Hata ({ex.Message})";
                ConnectionStatusText.Foreground = Brushes.Red;
                StartVideoButton.IsEnabled = false;
                SelectVideoButton.IsEnabled = false;
                ConnectButton.Visibility = Visibility.Collapsed; // ← yeni satır
                DisconnectButton.Visibility = Visibility.Visible;
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _isConnected = false;
            _heartbeatActive = false;
            try
            {
                if (_heartbeatThread != null && _heartbeatThread.IsAlive)
                {
                    _heartbeatThread.Join(500);
                    _heartbeatThread = null;
                }
            }
            catch { }
            try
            {
                if (_stream != null)
                {
                    _stream.Close();
                    _stream = null;
                }
            }
            catch { }
            try
            {
                if (_tcpClient != null)
                {
                    _tcpClient.Close();
                    _tcpClient = null;
                }
            }
            catch { }
            try
            {
                if (_vlcProcess != null && !_vlcProcess.HasExited)
                {
                    _vlcProcess.Kill();
                    _vlcProcess.Dispose();
                    _vlcProcess = null;
                }
            }
            catch { }
            StartVideoButton.IsEnabled = false;
            SelectVideoButton.IsEnabled = false;
            ConnectionStatusText.Text = "Durum: Bağlantı Yok";
            ConnectionStatusText.Foreground = Brushes.Gray;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ConnectButton.Visibility = Visibility.Visible; ;
        }

        private void SendHeartbeatLoop()
        {
            try
            {
                while (_heartbeatActive && _isConnected)
                {
                    SendHeartbeat();
                    Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ConnectionStatusText.Text = $"Heartbeat Hatası: {ex.Message}";
                    ConnectionStatusText.Foreground = Brushes.Red;
                    _isConnected = false;
                    _heartbeatActive = false;
                    StartVideoButton.IsEnabled = false;
                    SelectVideoButton.IsEnabled = false;
                });
            }
        }

        private void SendHeartbeat()
        {
            if (_stream == null) return;
            try
            {
                var writer = new BinaryWriter(_stream, Encoding.UTF8, true);
                writer.Write((byte)MessageType.Heartbeat);
                writer.Flush();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    ConnectionStatusText.Text = $"Heartbeat Gönderilemedi: {ex.Message}";
                    ConnectionStatusText.Foreground = Brushes.Red;
                    _isConnected = false;
                    _heartbeatActive = false;
                    StartVideoButton.IsEnabled = false;
                    SelectVideoButton.IsEnabled = false;
                });
            }
        }

        private void SendCoordinateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected || _stream == null) return;
            if (!double.TryParse(LatitudeTextBox.Text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lat)) return;
            if (!double.TryParse(LongitudeTextBox.Text.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double lon)) return;
            string desc = DescriptionTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(desc))
            {
                MessageBox.Show("Açıklama alanı boş olamaz!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var writer = new BinaryWriter(_stream, Encoding.UTF8, true);
                writer.Write((byte)MessageType.Coordinate);
                writer.Write(lat);
                writer.Write(lon);
                var descBytes = Encoding.UTF8.GetBytes(desc);
                writer.Write(descBytes.Length);
                writer.Write(descBytes);
                writer.Flush();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Koordinat gönderilemedi!\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                _isConnected = false;
                _heartbeatActive = false;
                StartVideoButton.IsEnabled = false;
                SelectVideoButton.IsEnabled = false;
            }
        }

        private void SelectVideoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Önce sunucuya bağlanmalısınız!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ❗ Eğer zaten seçim yapılmışsa tekrar açma
            if (!string.IsNullOrEmpty(_selectedVideoPath))
                return;

            var dlg = new OpenFileDialog();
            dlg.Filter = "Video Dosyaları|*.mp4;*.avi;*.mov;*.mkv|Tüm Dosyalar|*.*";
            if (dlg.ShowDialog() == true)
            {
                _selectedVideoPath = dlg.FileName;
                VideoUrlTextBox.Text = _selectedVideoPath;
            }
        }

        private void StartVideoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isConnected)
            {
                MessageBox.Show("Önce sunucuya bağlanmalısınız!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string videoPath = VideoUrlTextBox.Text.Trim();
            if (string.IsNullOrEmpty(videoPath) || !System.IO.File.Exists(videoPath))
            {
                MessageBox.Show("Geçerli bir video dosyası seçiniz!", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string vlcPath = @"C:\\Program Files\\VideoLAN\\VLC\\vlc.exe";
            if (!System.IO.File.Exists(vlcPath))
            {
                MessageBox.Show($"VLC Media Player bulunamadı!\nBeklenen yol: {vlcPath}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string rawIp = ((System.Net.IPEndPoint)_tcpClient.Client.LocalEndPoint).Address.ToString();
            if (rawIp.Contains(":") && rawIp.Contains("."))
            {
                int lastColon = rawIp.LastIndexOf(':');
                rawIp = rawIp.Substring(lastColon + 1);  // sadece IPv4 kısmını al
            }
            string streamUrl = $"rtsp://{rawIp}:8554/";


            // Her yayında benzersiz log dosyası
            string logFile = Path.Combine(Path.GetTempPath(), $"vlc-log-{DateTime.Now:yyyyMMdd_HHmmssfff}.txt");
            string args = $"\"{videoPath}\" --sout=\"#rtp{{sdp={streamUrl}}}\" --sout-keep --intf dummy --no-video-title-show --file-logging --logfile=\"{logFile}\"";



            try
            {
                if (_vlcProcess != null && !_vlcProcess.HasExited)
                {
                   
                    return;
                }
                _vlcProcess = new Process();
                _vlcProcess.StartInfo.FileName = vlcPath;
                _vlcProcess.StartInfo.Arguments = args;
                _vlcProcess.StartInfo.UseShellExecute = false;
                _vlcProcess.StartInfo.CreateNoWindow = true;
                bool started = _vlcProcess.Start();
                if (!started)
                {
                    MessageBox.Show("VLC process başlatılamadı!", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                // Kısa bir süre bekle, log dosyası oluştu mu kontrol et
                System.Threading.Thread.Sleep(1000);
                if (!System.IO.File.Exists(logFile))
                {
                    MessageBox.Show($"VLC log dosyası oluşturulamadı!\nArgümanlar: {args}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                // Log dosyasını oku, hata var mı kontrol et
                try
                {
                    string logContent = System.IO.File.ReadAllText(logFile);
                    if (logContent.Contains("error") || logContent.Contains("failed") || logContent.Contains("hata"))
                    {
                        MessageBox.Show($"VLC logunda hata bulundu!\n{logContent}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
                catch (IOException)
                {
                   

                }
                MessageBox.Show($"Video yayını başlatıldı!\nRTSP adresi: rtsp://<IP_adresin>:8554/\nServerApp'te bu adresi izleyebilirsin.", "Bilgi");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Video yayını başlatılamadı!\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SelectOnMapButton_Click(object sender, RoutedEventArgs e)
        {
            SelectOnMapButton.IsEnabled = false;

            string htmlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Map", "map.html");
            string url = $"file:///{htmlPath.Replace("\\", "/")}";
            var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            var wnd = new Window { Title = "Haritadan Koordinat Seç", Content = webView, Width = 800, Height = 600 };
            webView.Source = new Uri(url);

            EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs>? handler = null;

            webView.CoreWebView2InitializationCompleted += (s, ev) =>
            {
                handler = (s2, ev2) =>
                {
                    try
                    {
                        dynamic msg = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ev2.WebMessageAsJson);
                        double lat = msg.GetProperty("lat").GetDouble();
                        double lng = msg.GetProperty("lng").GetDouble();
                        LatitudeTextBox.Text = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        LongitudeTextBox.Text = lng.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch { }

                    // ✔ handler'ı hemen çıkar
                    if (webView.CoreWebView2 != null)
                        webView.CoreWebView2.WebMessageReceived -= handler!;

                    wnd.Close();
                    SelectOnMapButton.IsEnabled = true;
                };

                webView.CoreWebView2.WebMessageReceived += handler;
            };

            wnd.Closed += (s, ev) =>
            {
                if (webView.CoreWebView2 != null && handler != null)
                    webView.CoreWebView2.WebMessageReceived -= handler;

                SelectOnMapButton.IsEnabled = true;
            };

            wnd.ShowDialog();
        }



        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_vlcProcess != null && !_vlcProcess.HasExited)
            {
                _vlcProcess.Kill();
                _vlcProcess.Dispose();
                _vlcProcess = null;
            }
        }
    }
}