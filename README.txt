# ServerApp & ClientApp (WPF - RTSP Streaming, TCP Communication, Video Overlay)

Bu proje iki uygulamadan oluşmaktadır:

- **ClientApp**: Koordinat gönderimi, video yayını başlatma ve kalp atışı (heartbeat) mesajlarını göndermekten sorumludur.
- **ServerApp**: Gelen TCP bağlantılarını dinler, harita veya video paneli üzerinden izleme yapar, koordinatları listeler ve nişangâh, zaman gibi overlay verilerini video üzerine uygular.

---

## 🔧 Özellikler

### ClientApp

- 📡 **RTSP Yayını Başlatma**: VLC altyapısıyla yerel video dosyasını RTSP üzerinden yayınlar.
- 💬 **TCP Mesajlaşma**: Sunucuya periyodik heartbeat ve koordinat mesajları gönderir.
- 🗺️ **Harita Seçimi**: WebView2 üzerinden harita görüntüsü ve koordinat seçimi yapılabilir.
- 🎯 **Kullanıcı Etkileşimi**: Arayüz üzerinden video başlat, koordinat gönder, mermi ekle gibi işlemler yapılabilir.

### ServerApp

- 🔌 **TCP Sunucu**: Client’lardan gelen bağlantıları listeler ve gelen mesajları işler.
- 📺 **RTSP Video İzleme**: Client’tan gelen RTSP yayını izlenebilir.
- 🧭 **Overlay Sistemi**: Video üzerine mermi sayısı, tarih/saat ve nişangâh (+) gibi bilgiler çizilir.
- 📊 **Veri Listesi**: Gelen koordinatlar ayrı bir tabloda listelenir.
- 🖼️ **Tek Panelde Gösterim**: Video veya harita tek bir panelde dinamik olarak gösterilir (MainContentArea).

---

## 🧱 Proje Yapısı

── ClientApp/
│   ├── MainWindow.xaml (.cs)
│   ├── map.html (Harita)
│   ├── VLC + WebView2 bağımlılıkları
│
├── ServerApp/
│   ├── MainWindow.xaml (.cs)
│   ├── LibVLCSharp entegrasyonu
│   ├── RTSP izleyici ve sembol çizer

---

## ▶️ Kurulum ve Çalıştırma

### Gereksinimler:
- .NET 8.0 SDK
- VLC Media Player (RTSP yayını için)
- LibVLCSharp
- OpenCvSharp4
- WebView2 Runtime (ClientApp için)

### Çalıştırmak için:

1. **VLC RTSP Yayınlamayı Ayarla (ClientApp):**
   - `--sout=#rtp{sdp=rtsp://:8554/}` parametresiyle VLC başlatılır.
   - Yayın: `rtsp://127.0.0.1:8554/`

2. **ClientApp:**
   - Açılır, kullanıcı tarafından video seçilir.
   - Yayın başlatılır ve ServerApp’e TCP ile koordinat gönderilir.

3. **ServerApp:**
   - Uygulama açılır, `Video İzle` paneli üzerinden gelen yayını izlemeye başlar.
   - Gelen koordinatları listeler ve videoya + nişangâh overlay'i uygular.

---

## 🛠️ Geliştirici Notları

- `VideoDisplay()` fonksiyonu ve timer’lar çok hassastır. Dispatcher üzerinden UI erişimleri thread-safe yapılmalıdır.
- `GC.Collect()` çağrısı kaldırılmıştır. Bellek yönetimi manuel yapılmamalıdır.
- `wnd.Closed` ve `closeBtn.Click` olayları senkronize çalışmalıdır.
- `MainContentArea` dinamik içerik için kullanılır. Harita/video arasında geçişler buradan yönetilir.

---