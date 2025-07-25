using System.Text;
using System.Windows;
using System.Net;
using System.Net.Sockets;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using System.Windows.Controls;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using MediaPlayerVLC = LibVLCSharp.Shared.MediaPlayer;
using OpenCvSharp;

namespace ServerApp
{
    public partial class MainWindow : System.Windows.Window
    {
        private TcpListener? _tcpListener;
        private bool _isServerRunning = false;
        private DateTime _lastHeartbeat = DateTime.MinValue;
        private System.Timers.Timer? _heartbeatCheckTimer;
        private int _bulletCount = 100;
        private string? _lastClientIp = null;
        private System.Windows.Window? _videoWindow;


        private IntPtr DummyLock(IntPtr opaque, IntPtr planes) => IntPtr.Zero;
        private void DummyUnlock(IntPtr opaque, IntPtr picture, IntPtr planes) { }
        private void DummyDisplay(IntPtr opaque, IntPtr picture) { }




        public class CoordinateItem
        {
            public string? Timestamp { get; set; }
            public string? Latitude { get; set; }
            public string? Longitude { get; set; }
            public string? Description { get; set; }
        }
        private ObservableCollection<CoordinateItem> _coordinates = new();

        public class ClientInfo
        {
            public string? Id { get; set; }
            public string? Status { get; set; }
            public DateTime LastHeartbeat { get; set; }
        }
        private ObservableCollection<ClientInfo> _clients = new();

        private enum MessageType : byte
        {
            Heartbeat = 1,
            Coordinate = 2
        }

        private LibVLC? _localCameraLibVLC;
        private MediaPlayerVLC? _localCameraPlayer;
        private WriteableBitmap? _cameraBitmap;
        private object _bitmapLock = new object();
        private System.Windows.Threading.DispatcherTimer? _overlayTimer;
        private LibVLCSharp.WPF.VideoView? _localCameraView;

        // VideoCallbacks struct'ı ve callback fonksiyonları sınıfın içine taşındı
        public struct VideoCallbacks
        {
            public LibVLCSharp.Shared.MediaPlayer.LibVLCVideoLockCb Lock;
            public LibVLCSharp.Shared.MediaPlayer.LibVLCVideoUnlockCb Unlock;
            public LibVLCSharp.Shared.MediaPlayer.LibVLCVideoDisplayCb Display;
        }

        private IntPtr VideoLock(IntPtr opaque, IntPtr planes)
        {
            IntPtr bufferPtr = IntPtr.Zero;

            Dispatcher.Invoke(() =>
            {
                if (_cameraBitmap == null) return;
                bufferPtr = _cameraBitmap.BackBuffer;
            });

            if (_cameraBitmap == null) return IntPtr.Zero;

            lock (_bitmapLock)
            {
                Marshal.WriteIntPtr(planes, bufferPtr);
            }

            return IntPtr.Zero;
        }

        private void VideoUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        {
            if (Dispatcher.HasShutdownStarted || _cameraBitmap == null) return;

            _cameraBitmap.Dispatcher.BeginInvoke(() =>
            {
                lock (_bitmapLock)
                {
                    _cameraBitmap.Lock();
                    DrawOverlay(_cameraBitmap);
                    _cameraBitmap.AddDirtyRect(new Int32Rect(0, 0, _cameraBitmap.PixelWidth, _cameraBitmap.PixelHeight));
                    _cameraBitmap.Unlock();
                }
            });
        }

        private void VideoDisplay(IntPtr opaque, IntPtr picture)
        {
            if (Dispatcher.HasShutdownStarted || _cameraBitmap == null) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    _cameraBitmap?.Lock();
                    _cameraBitmap?.AddDirtyRect(new Int32Rect(0, 0, _cameraBitmap.PixelWidth, _cameraBitmap.PixelHeight));
                }
                finally
                {
                    _cameraBitmap?.Unlock();
                }
            });
        }

        // MainWindow sınıfına ek alanlar:
        private VideoCapture? _cvCamera;
        private WriteableBitmap? _cvBitmap;
        private System.Windows.Threading.DispatcherTimer? _cvTimer;
       

        public MainWindow()
        {
            InitializeComponent();
            CoordinateDataGrid.ItemsSource = _coordinates;
            ClientListDataGrid.ItemsSource = _clients;
            _clients.Clear();

            BulletCountTextBox.Text = _bulletCount.ToString();
            OpenVideoPanelButton.Click += OpenVideoPanelButton_Click;


        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isServerRunning) return;
            string ip = IpTextBox.Text.Trim();
            int port = int.Parse(PortTextBox.Text.Trim());
            Task.Run(() => StartServer(ip, port));
        }

        private void StartServer(string ip, int port)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Parse(ip), port);
                _tcpListener.Start();
                _isServerRunning = true;
                Dispatcher.Invoke(() => NotificationText.Text = "Durum: Açık");
                Task.Run(() => AcceptClients());
                StartHeartbeatCheck();
            }
            catch
            {
                Dispatcher.Invoke(() => NotificationText.Text = "Durum: Hata");
            }
        }

        private void AcceptClients()
        {
            while (_isServerRunning)
            {
                try
                {
                    var client = _tcpListener.AcceptTcpClient();
                    string clientId = ((IPEndPoint)client.Client.RemoteEndPoint).ToString();
                    Dispatcher.Invoke(() => AddOrUpdateClient(clientId, "Bekleniyor"));
                    Task.Run(() => HandleClient(client, clientId));
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => NotificationText.Text = $"Sunucu Hatası: {ex.Message}");
                    Thread.Sleep(1000);
                }
            }
        }

        private void HandleClient(TcpClient client, string clientId)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new BinaryReader(stream);

                while (client.Connected)
                {
                    try
                    {
                        int typeByte = reader.ReadByte();
                        if (typeByte == -1) break; // Stream kapanmış

                        MessageType msgType = (MessageType)typeByte;

                        if (msgType == MessageType.Heartbeat)
                        {
                            Dispatcher.Invoke(() => {
                                var c = _clients.FirstOrDefault(x => x.Id == clientId);
                                if (c != null)
                                {
                                    c.LastHeartbeat = DateTime.Now;
                                    AddOrUpdateClient(clientId, "Aktif");
                                }
                            });
                        }
                        else if (msgType == MessageType.Coordinate)
                        {
                            double lat = reader.ReadDouble();
                            double lon = reader.ReadDouble();
                            int len = reader.ReadInt32();
                            string desc = Encoding.UTF8.GetString(reader.ReadBytes(len));

                            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                            Dispatcher.Invoke(() =>
                            {
                                var newItem = new CoordinateItem
                                {
                                    Timestamp = now,
                                    Latitude = lat.ToString("G17", CultureInfo.InvariantCulture),
                                    Longitude = lon.ToString("G17", CultureInfo.InvariantCulture),
                                    Description = desc
                                };
                                _coordinates.Add(newItem);
                                ShowOnMap(newItem);
                                ShowNotification($"YENİ KOORDİNAT: {desc}");
                            });
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        // Stream sonlandı, bağlantı kapandı
                        break;
                    }
                    catch (IOException ioEx)
                    {
                        Dispatcher.Invoke(() => NotificationText.Text = $"Bağlantı kesildi: {ioEx.Message}");
                        break;
                    }
                    catch (Exception innerEx)
                    {
                        Dispatcher.Invoke(() => NotificationText.Text = $"Hata: {innerEx.Message}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => NotificationText.Text = $"Bağlantı Hatası: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() => AddOrUpdateClient(clientId, "Pasif"));
            }
        }


        private void AddOrUpdateClient(string id, string status)
        {
            var existing = _clients.FirstOrDefault(c => c.Id == id);
            if (existing != null)
            {
                existing.Status = status;
                if (status == "Aktif") existing.LastHeartbeat = DateTime.Now;
                ClientListDataGrid.Items.Refresh();
            }
            else
            {
                _clients.Add(new ClientInfo { Id = id, Status = status, LastHeartbeat = DateTime.Now });
            }
            if (!string.IsNullOrEmpty(id))
            {
                var ip = id.Split(':')[0];
                _lastClientIp = ip;
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            NotificationText.Text = "";
            //string ip = "127.0.0.1"; // veya varsayılan IP textbox’tan da alınabilir
            //int port = 5000;         // uygun bir varsayılan port

            string ip = IpTextBox.Text.Trim();
            int port = int.Parse(PortTextBox.Text.Trim());
            Task.Run(() => StartServer(ip, port));

            ConnectButton.Visibility = Visibility.Collapsed; // butonu gizle

        }

        private void StartHeartbeatCheck()
        {
            _heartbeatCheckTimer = new(2000);
            _heartbeatCheckTimer.Elapsed += (s, e) =>
            {
                Dispatcher.Invoke(() =>
                {
                    foreach (var client in _clients)
                    {
                        if (client.Status == "Pasif") continue;
                        if ((DateTime.Now - client.LastHeartbeat).TotalSeconds > 3)
                        {
                            client.Status = "Bekleniyor";
                        }
                        else
                        {
                            client.Status = "Aktif";
                        }
                    }
                    ClientListDataGrid.Items.Refresh();
                });
            };
            _heartbeatCheckTimer.Start();
        }

        private async void ShowNotification(string text)
        {
            NotificationText.Text = text;
            NotificationText.Visibility = Visibility.Visible;
            await Task.Delay(2000);
            NotificationText.Visibility = Visibility.Collapsed;
        }

        private void ShowOnMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CoordinateItem item)
            {
                ShowOnMap(item);
            }
        }

        private void ShowOnMap(CoordinateItem item)
        {
            Dispatcher.Invoke(() =>
            {
                Debug.WriteLine($"[DEBUG] ShowOnMap: Latitude={item.Latitude}, Longitude={item.Longitude}");
                double lat, lon;
                bool latOk = double.TryParse(item.Latitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lat);
                bool lonOk = double.TryParse(item.Longitude, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out lon);
                Debug.WriteLine($"[DEBUG] Parse latOk={latOk}, lonOk={lonOk}, lat={lat}, lon={lon}");
                if (latOk && lonOk)
                {
                    string desc = Uri.EscapeDataString(item.Description ?? "");
                    string htmlPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Map", "map.html");
                    string url = $"file:///{htmlPath.Replace("\\", "/")}?lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}&lng={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&desc={desc}";
                    Debug.WriteLine($"[DEBUG] URL: {url}");
                    MainContentArea.Content = null;
                    var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
                    webView.AllowDrop = false;
                    webView.CoreWebView2InitializationCompleted += (s, e) =>
                    {
                        if (!e.IsSuccess)
                        {
                            Debug.WriteLine($"[DEBUG] WebView2 başlatılamadı: {e.InitializationException}");
                        }
                    };
                    webView.NavigationCompleted += (s, e) =>
                    {
                        if (!e.IsSuccess)
                        {
                            Debug.WriteLine($"[DEBUG] Navigation hatası: {e.WebErrorStatus}");
                        }
                    };
                    webView.Source = new Uri(url);
                    MainContentArea.Content = webView;
                    NotificationText.Text = $"Haritada gösteriliyor: {item.Description}";
                }
                else
                {
                    Debug.WriteLine("[DEBUG] Koordinat parse edilemedi!");
                    NotificationText.Text = "Koordinat hatalı!";
                }
            });
        }

        private void OpenVideoPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_videoWindow != null && _videoWindow.IsVisible)
            {
                _videoWindow.Activate();
                return;
            }

            _cvTimer?.Stop(); _cvTimer = null;
            _cvCamera?.Release(); _cvCamera = null;
            _cvBitmap = null;

            var wnd = new System.Windows.Window { Title = "Video İzle", Width = 800, Height = 600 };
            _videoWindow = wnd;
            var root = new StackPanel { Margin = new Thickness(10) };

            string defaultUrl = _lastClientIp != null
                ? $"rtsp://{_lastClientIp}:8554/"
                : "rtsp://127.0.0.1:8554/";

            var urlBox = new TextBox { Width = 600, Text = defaultUrl };
            var startBtn = new Button { Content = "Başlat", Width = 100, Margin = new Thickness(5) };
            var stopBtn = new Button { Content = "Durdur", Width = 100, Margin = new Thickness(5), IsEnabled = false };
            var resumeBtn = new Button { Content = "Devam Et", Width = 100, Margin = new Thickness(5), IsEnabled = false };
            var closeBtn = new Button { Content = "Kapat", Width = 100, Margin = new Thickness(5) };

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            btnPanel.Children.Add(startBtn);
            btnPanel.Children.Add(stopBtn);
            btnPanel.Children.Add(resumeBtn);
            btnPanel.Children.Add(closeBtn);

            Image imgView = null;

            LibVLC libvlc = null;
            MediaPlayerVLC player = null;

            void Cleanup()
            {
                try { player?.Stop(); } catch { }
                player?.Dispose(); player = null;
                libvlc?.Dispose(); libvlc = null;

                Dispatcher.Invoke(() =>
                {
                    if (imgView != null)
                        imgView.Source = null;

                    MainContentArea.Content = null;
                });

                _overlayTimer?.Stop(); _overlayTimer = null;
                _videoWindow = null;

               
            }

            startBtn.Click += (_, __) =>
            {
                try
                {
                    player?.Stop(); player?.Dispose(); player = null;
                    libvlc?.Dispose(); libvlc = null;

                    Core.Initialize();
                    libvlc = new LibVLC();
                    player = new MediaPlayerVLC(libvlc);

                    int w = 640, h = 480;
                    _cameraBitmap = BitmapFactory.New(w, h);

                    if (imgView == null)
                    {
                        imgView = new Image { Source = _cameraBitmap, Stretch = Stretch.Uniform, Height = 400 };
                        root.Children.Add(imgView);
                    }
                    else
                    {
                        imgView.Source = _cameraBitmap;
                    }

                    player.SetVideoCallbacks(VideoLock, VideoUnlock, VideoDisplay);
                    player.SetVideoFormat("RV32", (uint)w, (uint)h, (uint)(w * 4));

                    var media = new Media(libvlc, urlBox.Text.Trim(), FromType.FromLocation);
                    player.Play(media);

                    stopBtn.IsEnabled = true;
                    resumeBtn.IsEnabled = true;

                    player.Stopped += (_, __) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            imgView.Source = null;
                            MainContentArea.Content = null;
                        });
                    };
                }
                catch (Exception ex)
                {
                    Cleanup();
                    MessageBox.Show($"Video başlatılamadı!\n{ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                    stopBtn.IsEnabled = resumeBtn.IsEnabled = false;
                }
            };

            stopBtn.Click += (_, __) =>
            {
                if (player?.IsPlaying == true) player.Pause();
            };

            resumeBtn.Click += (_, __) =>
            {
                if (player?.IsPlaying == false) player.Play();
            };

            closeBtn.Click += (_, __) =>
            {
                try
                {
                    // 🔴 1. VLC callback'lerini iptal et — en başta!
                    player?.SetVideoCallbacks(DummyLock, DummyUnlock, DummyDisplay);

                    // 🔴 2. Stop ve Dispose işlemi
                    if (player != null)
                    {
                        player.Stop(); // önce durdur
                        player.Dispose();
                        player = null;
                    }

                    if (libvlc != null)
                    {
                        libvlc.Dispose();
                        libvlc = null;
                    }

                    // 🔴 3. Dispatcher, Timer, UI temizliği
                    Dispatcher.Invoke(() => MainContentArea.Content = null);
                    _overlayTimer?.Stop();
                    _overlayTimer = null;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Kapatma hatası: {ex.Message}");
                }

                // 🔴 4. Son olarak pencereyi kapat
                _videoWindow?.Close();
                _videoWindow = null;
                GC.Collect();
            };



            wnd.Closed += (_, __) => Cleanup();

            root.Children.Add(new TextBlock { Text = "Video/Kamera URL'si:" });
            root.Children.Add(urlBox);
            root.Children.Add(btnPanel);
            wnd.Content = root;
            wnd.Show();
        }

        private void StartLocalCameraButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                _localCameraLibVLC?.Dispose();
                _localCameraPlayer?.Stop();
                _localCameraPlayer?.Dispose();

                _localCameraLibVLC = new LibVLC();
                _cameraBitmap = BitmapFactory.New(640, 480);

                var videoCallbacks = new VideoCallbacks
                {
                    Lock = VideoLock,
                    Unlock = VideoUnlock,
                    Display = VideoDisplay
                };

                _localCameraPlayer = new MediaPlayerVLC(_localCameraLibVLC);
                _localCameraPlayer.SetVideoFormat("RV32", 640, 480, 640 * 4);
                _localCameraPlayer.SetVideoCallbacks(videoCallbacks.Lock, videoCallbacks.Unlock, videoCallbacks.Display);

                // Bu satır en önemli kısmı: ilk bulduğu kamerayı kullan
                var media = new Media(_localCameraLibVLC, "dshow://", FromType.FromLocation);
                media.AddOption(":dshow-vdev");  // Kamerayı elle seçmeden aç

                _localCameraPlayer.Play(media);

                Dispatcher.Invoke(() =>
                {
                    var image = new System.Windows.Controls.Image
                    {
                        Source = _cameraBitmap,
                        Stretch = Stretch.Uniform
                    };
                    MainContentArea.Content = image;
                    StartCvCamera(0);
                    NotificationText.Text = "Kamera yayını başlatıldı.";
                });
            }
            catch (Exception ex)
            {
                NotificationText.Text = $"Kamera hatası: {ex.Message}";
            }
        }

        private void DrawTextBoxed(WriteableBitmap bmp, string text, int x, int y, Color textColor, int fontSize, bool leftAlign, bool withIcon = false)
        {
            var dv = new DrawingVisual();
            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                fontSize,
                new SolidColorBrush(textColor),
                1.0);

            double iconWidth = withIcon ? fontSize : 0;
            double boxWidth = ft.Width + iconWidth + 20;
            double boxHeight = ft.Height + 10;

            using (var dc = dv.RenderOpen())
            {
                var blackBrush = new SolidColorBrush(Colors.Black);
                var redPen = new Pen(new SolidColorBrush(Colors.Red), 2);
                dc.DrawRectangle(blackBrush, redPen, new System.Windows.Rect(0, 0, boxWidth, boxHeight));
                if (withIcon)
                {
                    dc.DrawRectangle(new SolidColorBrush(Colors.White), null, new System.Windows.Rect(5, boxHeight / 2 - fontSize / 3, fontSize / 2, fontSize / 1.5));
                }
                double textX = withIcon ? (fontSize + 10) : 10;
                dc.DrawText(ft, new System.Windows.Point(textX, 5));
            }

            var rtb = new RenderTargetBitmap((int)boxWidth, (int)boxHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);

            int drawX = leftAlign ? x : x - (int)boxWidth;
            var rect = new Int32Rect(drawX, y, (int)boxWidth, (int)boxHeight);
            var pixels = new byte[(int)boxWidth * (int)boxHeight * 4];
            rtb.CopyPixels(pixels, (int)boxWidth * 4, 0);
            bmp.WritePixels(rect, pixels, (int)boxWidth * 4, 0);
        }

        private void DrawOverlay(WriteableBitmap bmp)
        {
            if (bmp == null) return;

            bmp.Lock();

            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            int cx = w / 2;
            int cy = h / 2;

            // --- artı nişangâh ---
            int len = 25, thick = 2;
            int argbRed = unchecked((int)0xFFFF0000);
            for (int i = -len; i <= len; i++)
            {
                for (int t = -thick; t <= thick; t++)
                {
                    bmp.SetPixel(cx + i, cy + t, argbRed); // yatay
                    bmp.SetPixel(cx + t, cy + i, argbRed); // dikey
                }
            }

            // --- mermi kutusu (sol-alt) ---
            DrawTextBoxed(bmp,
                          _bulletCount.ToString(),
                          10,
                          h - 50,               // 10 px kenar boşluğu + tahm. 40 px kutu
                          Colors.White,
                          28,
                          leftAlign: true,
                          withIcon: true);

            // --- tarih/saat kutusu (sağ-alt) ---
            DrawTextBoxed(bmp,
                          DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                          w - 10,               // sağ kenar
                          h - 44,               // alt kenar
                          Colors.White,
                          24,
                          leftAlign: false,
                          withIcon: false);

            bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
            bmp.Unlock();
        }

        // Kamera başlat
        private void StartCvCamera(int cameraIndex)
        {
            // VLC ile ilgili player ve bitmap'i tamamen dispose et
            _localCameraPlayer?.Stop();
            _localCameraPlayer?.Dispose();
            _localCameraPlayer = null;
            _localCameraLibVLC?.Dispose();
            _localCameraLibVLC = null;
            _cameraBitmap = null;

            _cvCamera?.Release();
            _cvCamera = new VideoCapture(cameraIndex);
            if (!_cvCamera.IsOpened())
            {
                MessageBox.Show($"Kamera {cameraIndex} açılamadı!");
                return;
            }

            int width = (int)_cvCamera.Get(VideoCaptureProperties.FrameWidth);
            int height = (int)_cvCamera.Get(VideoCaptureProperties.FrameHeight);

            // ⬇ 32-bit BGRA bitmap
            _cvBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            var img = new Image { Source = _cvBitmap, Stretch = Stretch.Uniform };
            MainContentArea.Content = img;

            _cvTimer?.Stop();
            _cvTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _cvTimer.Tick += (s, e) =>
            {
                using var matBgr = new Mat();
                if (!_cvCamera.Read(matBgr) || matBgr.Empty()) return;

                // BGR → BGRA
                using var matBgra = new Mat();
                Cv2.CvtColor(matBgr, matBgra, ColorConversionCodes.BGR2BGRA);

                _cvBitmap.Lock();
                unsafe
                {
                    Buffer.MemoryCopy((void*)matBgra.DataPointer,
                                      (void*)_cvBitmap.BackBuffer,
                                      _cvBitmap.BackBufferStride * height,
                                      matBgra.Step() * height);
                }
                _cvBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                _cvBitmap.Unlock();

                DrawOverlay(_cvBitmap);   // kendi kilitliyor
            };
            _cvTimer.Start();
        }
        // DrawOverlay fonksiyonu aynen kullanılabilir (artı, mermi, tarih/saat)
        // ... mevcut DrawOverlay fonksiyonunu kullan ...

        private void AddBulletButton_Click(object sender, RoutedEventArgs e)
        {
            _bulletCount += 100;
            BulletCountTextBox.Text = _bulletCount.ToString();

            if (_cameraBitmap != null) DrawOverlay(_cameraBitmap);
            if (_cvBitmap != null) DrawOverlay(_cvBitmap);
        }

        private void FireButton_Click(object sender, RoutedEventArgs e)
        {
            if (_bulletCount > 0)
            {
                _bulletCount--;
                BulletCountTextBox.Text = _bulletCount.ToString();

                // ↓ değişiklik
                if (_cameraBitmap != null) DrawOverlay(_cameraBitmap);
                if (_cvBitmap != null) DrawOverlay(_cvBitmap);
            }
        }

        private void StopLocalCameraButton_Click(object sender, RoutedEventArgs e)
        {
            _cvTimer?.Stop();
            _cvTimer = null;

            _cvCamera?.Release();
            _cvCamera = null;

            _cvBitmap = null;

            MainContentArea.Content = null;
            NotificationText.Text = "Kamera yayını durduruldu.";
        }

    }
}

