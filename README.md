**Türkçe** | [English](README.en.md)

# Multi Bluetooth Audio Router

[![CI](https://github.com/burhanbty/multi-bluetooth-audio-router/actions/workflows/ci.yml/badge.svg)](https://github.com/burhanbty/multi-bluetooth-audio-router/actions/workflows/ci.yml)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Lisans: MIT](https://img.shields.io/badge/Lisans-MIT-yellow.svg)](LICENSE)

Bu proje, Windows'ta çalan sesi aynı anda iki farklı kulaklığa veya hoparlöre yönlendirmek için geliştirdiğim deneysel bir masaüstü uygulaması. İki çıkış için ayrı gecikme ayarı sunuyor; ayrıca bir cihaz kombinasyonu çalışmadığında sorunun nereden kaynaklanabileceğini anlamaya yardımcı olan WASAPI tanılama araçları içeriyor.

> **Projenin durumu:** Uygulamanın yönlendirme motoru çalışıyor, ancak sonuç Bluetooth adaptörüne, sürücülere, kullanılan codec'lere ve Windows'un oluşturduğu ses uç noktalarına bağlı. Donanım aynı anda yalnızca tek bir A2DP akışına izin veriyorsa bunu yazılımla aşmak mümkün değil.

![Uygulama görünümü](docs/assets/app-overview.png)

## Bu proje neden ortaya çıktı?

Başlangıçtaki fikir oldukça basitti: Windows'ta çalan sesi yakala ve iki ayrı çıkışa gönder. Uygulamaya başlayınca işin yalnızca sesi iki kez oynatmaktan ibaret olmadığını gördüm.

Windows her kulaklığı ve hoparlörü ayrı bir ses uç noktası olarak yönetiyor. Her çıkışın desteklediği format, tampon davranışı ve gecikmesi farklı olabiliyor. Bluetooth tarafında ise sürücü veya adaptör, iki cihaz da tek başına sorunsuz çalışsa bile ikinci yüksek kaliteli ses akışını açmayı reddedebiliyor.

Bu nedenle proje zamanla yalnızca bir ses yönlendirici olmaktan çıkıp “neden çalışmadı?” sorusuna da cevap vermeye çalışan bir araca dönüştü.

## Karşıma çıkan temel problemler

### İki cihaz tek başına çalışıyor, birlikte çalışmıyor

En yanıltıcı durumlardan biri buydu. İki çıkış da ayrı ayrı açılabiliyor, fakat ikincisi devreye girdiğinde Windows `AUDCLNT_E_ENDPOINT_CREATE_FAILED` gibi bir hata döndürebiliyor. Bu her zaman uygulama hatası anlamına gelmiyor; ortak sürücü veya Bluetooth kaynağının sınırına ulaşılmış olabilir.

Bu ayrımı yapabilmek için uygulama çıkışları önce tek tek, ardından iki farklı açılış sırasıyla deniyor. Sonuçları cihaz kaynaklı hata, sıra duyarlılığı, format dönüşümü sorunu veya olası ortak kaynak sınırı olarak sınıflandırıyor.

### Açılış sırası sonucu değiştirebiliyor

Bazen “önce A, sonra B” sırası başarısız olurken ters sıra çalışabiliyor. Bu yüzden yalnızca tek bir deneme yapmak güvenilir bir sonuç vermiyordu. Hızlı uyumluluk kontrolü ve ayrıntılı donanım tanılaması her iki sırayı da özellikle test ediyor.

### Her çıkış aynı ses formatını kabul etmiyor

Bir cihazın örnekleme hızı veya kanal düzeni diğerinden farklı olabiliyor. Her çıkış için ayrı bir dönüştürme zinciri kurularak kaynak ses, cihazın Windows karışım formatına uyarlanıyor. Burada NAudio ve Media Foundation kullanılıyor.

### Aynı anda başlatmak tam senkronizasyon sağlamıyor

İki oynatıcıyı art arda başlatmak bile duyulabilir bir fark oluşturabiliyor. Bunu azaltmak için çıkışlar ortak bir başlangıç tamponu dolduktan sonra mümkün olduğunca yakın zamanda başlatılıyor. Yine de bağımsız Bluetooth cihazlarının ortak bir donanım saati yok; zaman içinde küçük kaymalar oluşabilir. Uygulamadaki manuel gecikme ve tıklama testi bu farkı pratik olarak ayarlamak için var.

### Geri besleme riski

Kaynak ile çıkışlardan biri aynı cihaz olursa yakalanan ses tekrar sisteme girerek döngü oluşturabilir. Bu nedenle kaynak ve iki çıkışın birbirinden farklı olması zorunlu. Sistem sesini güvenli biçimde yakalamak için sanal bir ses kablosu kullanmak en temiz yöntem.

## Uygulama neler sunuyor?

- Seçilen Windows oynatma aygıtından WASAPI loopback ile ses yakalama
- Sesi aynı anda iki farklı çıkışa yönlendirme
- Her çıkış için 0–3000 ms aralığında, 10 ms adımlı gecikme ayarı
- Her cihaz için ayrı ses formatı dönüşümü
- Başlangıç farkını azaltan ortak ön tampon
- Hızlı ve önbellekli uyumluluk kontrolü
- Cihazları tek tek ve iki farklı sırada deneyen ayrıntılı tanılama
- Bilinen WASAPI hata kodlarını anlaşılır sınıflara dönüştürme
- Ses dosyasıyla çıkış testi ve tıklama sesiyle gecikme kalibrasyonu
- Tampon doluluk, taşma ve yetersiz veri durumlarını gösteren canlı telemetri

## Gereksinimler

- Windows 10 veya Windows 11
- Kaynak koddan derlemek için [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows'ta görünen iki farklı oynatma aygıtı
- Sistem sesini geri besleme olmadan yönlendirmek için tercihen sanal bir ses kablosu

## Derleme ve çalıştırma

```powershell
git clone https://github.com/burhanbty/multi-bluetooth-audio-router.git
cd multi-bluetooth-audio-router
dotnet restore MultiBluetoothAudioRouter.sln --locked-mode
dotnet build MultiBluetoothAudioRouter.sln --configuration Release --no-restore
dotnet run --project MultiBluetoothAudioRouter/MultiBluetoothAudioRouter.csproj --configuration Release
```

Sınıflandırma ve hata eşleme testlerini çalıştırmak için:

```powershell
dotnet run --project MultiBluetoothAudioRouter.Tests/MultiBluetoothAudioRouter.Tests.csproj --configuration Release --no-build
```

## Önerilen kullanım düzeni

1. Windows'un varsayılan ses çıkışını VB-CABLE veya benzeri bir sanal ses kablosu yapın.
2. Uygulamada bu sanal aygıtı **Source Device** olarak seçin.
3. Dinlemek istediğiniz iki farklı kulaklığı veya hoparlörü çıkış olarak seçin.
4. Yönlendirmeyi başlatmadan önce hızlı uyumluluk kontrolünü çalıştırın.
5. Gerekirse tıklama testiyle iki cihazı dinleyin ve erken duyulan çıkışa gecikme ekleyin.

Kaynak, iki çıkıştan da farklı olmalı; iki çıkış da aynı aygıt olmamalı. Uygulama geri besleme ve hatalı yönlendirme riskini azaltmak için bu kuralı kontrol ediyor.

## Projenin yapısı

WPF katmanı cihaz seçimini ve kullanıcı etkileşimlerini yönetiyor. Ses yakalama, çıkış oturumları, tamponlama, format dönüşümü, hata sınıflandırma ve tanılama işlemleri ayrı bileşenlerde tutuluyor. Daha teknik bir görünüm için [mimari dokümanına](docs/architecture.md) bakabilirsiniz.

## Bilinen sınırlar

- Bağımsız Bluetooth cihazları tam anlamıyla örnek seviyesinde senkron tutulamaz ve zamanla kayabilir.
- Klasik Bluetooth adaptörlerinin eşzamanlı A2DP sınırları yazılımla kaldırılamaz.
- Windows yeterli cihaz bilgisi vermediğinde bağlantı türü, açıkça sezgisel olarak işaretlenen isim tabanlı tahminlerle belirlenir.
- Gecikme değişiklikleri yönlendirme başlatılırken uygulanır; aktif yönlendirmeyi durdurup yeniden başlatmak gerekir.
- WPF, WASAPI ve Media Foundation kullanıldığı için proje yalnızca Windows'ta çalışır.

## Gizlilik

Ses verisi bilgisayardan dışarı gönderilmez. Uygulamada analiz servisi, kullanıcı hesabı, ağ istemcisi veya buluta yükleme özelliği bulunmuyor. Tanılama raporları yerel aygıt adlarını ve cihaz kimliklerini içerebilir; bu raporları herkese açık paylaşmadan önce gözden geçirmeniz iyi olur.

## Katkıda bulunmak

Bir hata bildirirken Windows sürümünü, adaptör/sürücü bilgisini, kullandığınız çıkış türlerini ve mümkünse kişisel bilgileri temizlenmiş tanılama raporunu eklemeniz sorunu anlamayı kolaylaştırır. Kod katkıları için [CONTRIBUTING.md](CONTRIBUTING.md) dosyasına göz atabilirsiniz.

## Lisans

Proje [MIT Lisansı](LICENSE) ile yayımlanıyor.
