# 01 — Proje Özeti

## 1. Projenin Amacı

Interview AI projesinin amacı; kullanıcıların teknik ve davranışsal mülakatlara hazırlanmasını sağlayan, interaktif bir mülakat simülasyon platformu geliştirmektir.

Sistem şu temel işlevleri yerine getirir:

- Kullanıcı hesabı oluşturma ve güvenli giriş (JWT tabanlı kimlik doğrulama)
- Mülakat türü, pozisyon ve zorluk seviyesi seçimi
- Gerçek zamanlı video ve ses kaydı ile mülakat oturumu başlatma
- Kamera üzerinden göz teması, duruş ve beden dili analizi (MediaPipe)
- Kullanıcının sesinin otomatik transkripte dönüştürülmesi (Whisper ASR)
- Oturum sonunda LLM destekli yetkinlik değerlendirmesi ve coaching raporu
- Soru bazlı puanlama, kategori bazlı geri bildirim ve gelişim önerileri

Amaç; adayların mülakat pratiğini ölçülebilir hale getirip, zayıf alanlarını hızlıca tespit ederek hedefli şekilde iyileştirmelerini sağlamaktır.

## 2. Projenin Hedefleri

### Kullanıcı Yönetimi
Kayıt, giriş ve temel profil bilgilerinin tutulması. JWT ile güvenli oturum yönetimi. Admin ve standart kullanıcı rolleri.

### Mülakat Oturumu Yönetimi
Mülakat türü/alan (teknik, davranışsal, HR), zorluk seviyesi, soru sayısı ve oturum durumlarının yönetilmesi. Her oturum benzersiz bir session ID ile takip edilir.

### Gerçek Zamanlı Video Kaydı
Kamera ve mikrofon üzerinden canlı mülakat deneyimi. Her soruya verilen yanıt video/ses olarak kaydedilir ve raporda erişilebilir olur.

### Otomatik Transkript
Whisper ASR servisi ile kullanıcının konuşması otomatik olarak yazıya dökülür. Transkript soru bazında segmentlere ayrılır ve raporla birlikte sunulur.

### Görüntü Tabanlı Davranış Analizi
MediaPipe Face Mesh ve Pose modülleri ile gerçek zamanlı olarak göz teması, duruş skoru, kafa titremesi (jitter), fidget (kıpırdanma) ve göz açıklığı metrikleri hesaplanır.

### Yapay Zeka Destekli Yetkinlik Değerlendirmesi
Transkript ve davranış metrikleri Claude API'ye gönderilerek teknik doğruluk, derinlik, yapı, netlik ve güven gibi boyutlarda yetkinlik puanlaması yapılır.

### Kapsamlı Raporlama
Oturum sonunda kullanıcıya sunulan rapor; genel skor, metrik bazlı dağılım, soru bazlı video kayıtları, tam transkript, AI coaching geri bildirimleri ve pratik alıştırma önerilerini içerir.

### Puanlama Profilleri
Farklı mülakat türleri (genel, teknik, HR) için ayrı ağırlık ve eşik değerleri tanımlanabilir. Profiller arasında geçiş yapılabilir ve rapor yeniden hesaplanabilir.

## 3. Araştırmanın Hipotezi

Gerçek zamanlı video analizi, otomatik transkript ve yapay zeka destekli yetkinlik değerlendirmesi sunan bir mülakat simülasyon sistemi; kullanıcıların tekrar eden pratikler sayesinde zayıf alanlarını hem davranışsal hem teknik boyutta daha hızlı tespit etmelerini ve mülakat başarı oranlarını artırmalarını sağlar.

## 4. Kısaltmalar

| Kısaltma | Açıklama |
|----------|----------|
| AI | Artificial Intelligence (Yapay Zekâ) |
| API | Application Programming Interface |
| ASR | Automatic Speech Recognition (Otomatik Konuşma Tanıma) |
| EF Core | Entity Framework Core |
| JWT | JSON Web Token |
| LLM | Large Language Model (Büyük Dil Modeli) |
| STT | Speech-to-Text (Konuşmadan Metne) |
| UI | User Interface (Kullanıcı Arayüzü) |
| CRUD | Create-Read-Update-Delete |

## 5. Projenin Kapsamı

### Kapsam İçi

- React + TypeScript frontend ile kullanıcı kayıt/giriş, mülakat oturumu, rapor ekranları
- ASP.NET Core Web API backend ile oturum yönetimi, metrik işleme, puanlama ve LLM orchestration
- PostgreSQL veritabanı ile oturum, soru, transkript, metrik ve skor kayıtları
- MediaPipe ile gerçek zamanlı görüntü analizi
- Whisper ASR ile otomatik transkript üretimi
- Claude API ile yetkinlik değerlendirmesi ve coaching raporu
- Ollama ile yerel LLM fallback desteği
- Swagger/OpenAPI ile API dokümantasyonu

### Kapsam Dışı (İlk Sürüm)

- Kurumsal ATS (Applicant Tracking System) entegrasyonu
- Çoklu kullanıcı aynı anda mülakat (panel mülakat)
- Mobil uygulama (sadece responsive web)
- Üretim ortamı yük dengeleme ve HA (High Availability)
- MFA (Multi-Factor Authentication)

## 6. Fonksiyonel Gereksinimler

| ID | Gereksinim |
|----|------------|
| FR_001 | Kullanıcı kayıt işlemi yapılabilmelidir |
| FR_002 | Kullanıcı giriş yapabilmeli ve JWT ile oturum yönetimi sağlanmalıdır |
| FR_003 | Kullanıcı profilinde deneyim seviyesi bilgisi tutulabilmelidir |
| FR_004 | Kullanıcı geçmiş mülakat listesini görebilmelidir |
| FR_005 | Mülakat başlatma ekranında tür/alan, dil ve zorluk seviyesi seçilebilmelidir |
| FR_006 | Sistem seçime göre mülakat oturumu oluşturmalı ve session ID üretmelidir |
| FR_007 | Mülakat oturumunda sorular sırayla gösterilmeli, video/ses kaydı başlatılabilmelidir |
| FR_008 | Her soru için kullanıcı yanıt kaydı (video/ses) saklanabilmelidir |
| FR_009 | Oturum sırasında görüntü analizi (göz teması, duruş, fidget) yapılmalıdır |
| FR_010 | Oturum sonunda ses kaydından otomatik transkript çıkarılmalıdır |
| FR_011 | Transkript ve metrikler LLM'e gönderilerek yetkinlik değerlendirmesi yapılmalıdır |
| FR_012 | Rapor sayfasında genel skor, metrik dağılımı ve AI coaching görüntülenmelidir |
| FR_013 | Raporda soru bazlı video kayıtları izlenebilmelidir |
| FR_014 | Raporda tam transkript görüntülenebilmelidir |
| FR_015 | Rapor JSON ve Markdown formatında dışa aktarılabilmelidir |
| FR_016 | Farklı puanlama profilleri (genel, teknik, HR) arasında geçiş yapılabilmelidir |
| FR_017 | Admin panelinden toplu coaching (batch) işlemi başlatılabilmelidir |
| FR_018 | Swagger/OpenAPI ile tüm endpoint'ler dokümante edilmelidir |

## 7. Fonksiyonel Olmayan Gereksinimler

- **Performans:** Rapor sayfası 2 saniye altında yüklenmelidir. LLM coaching çağrısı 120 saniye timeout ile çalışmalıdır.
- **Güvenlik:** Parolalar hash'li saklanmalı, API erişimi JWT ile korunmalı, kullanıcı verileri yetkilendirme ile erişilebilir olmalıdır.
- **Gizlilik:** Transkriptler isteğe bağlı redakte edilebilmeli, PII (kişisel bilgi) filtresi uygulanabilmelidir.
- **Kullanılabilirlik:** Arayüz sezgisel akışla (kayıt → giriş → mülakat → rapor) sunulmalıdır.
- **Genişletilebilirlik:** LLM provider değişikliği konfigürasyon ile yapılabilmeli, yeni scoring profilleri eklenebilmelidir.
- **Test Edilebilirlik:** Backend servisleri unit ve integration test yazımına uygun tasarlanmalıdır.

## 8. SWOT Analizi

| | |
|---|---|
| **Güçlü Yanlar** | **Zayıf Yanlar** |
| Gerçek zamanlı video + ses analizi | Tek geliştirici ile sınırlı hız |
| Claude API ile yüksek kalite coaching | Soru havuzu başlangıçta sınırlı |
| Çoklu LLM fallback mimarisi | Ollama coaching kalitesi Claude'a göre düşük |
| Modüler scoring profilleri | Mobil deneyim henüz optimize değil |
| Türkçe mülakat desteği ile farklılaşma | |
| | |
| **Fırsatlar** | **Tehditler** |
| Üniversite öğrencileri için yüksek talep | Benzer ticari ürünlerle rekabet |
| Kariyer merkezleri ile iş birliği | API maliyet/limit değişimleri |
| Türkçe NLP alanında az rakip | Kullanıcı verisi gizlilik riskleri |
| Davranışsal analiz ile benzersiz konum | |
