# 05 — Mülakat Oturum Akışı

## Genel Akış

Bir mülakat oturumu 5 ana aşamadan oluşur:

```
[1. Hazırlık] → [2. Kalibrasyon] → [3. Soru-Cevap] → [4. Finalize] → [5. Rapor]
```

## Aşama 1: Hazırlık

Kullanıcı Home sayfasından mülakata başlar. Seçim ekranında şunları belirler:

- **Mülakat Türü/Alan:** Frontend, Backend, DevOps, Genel Teknik, HR/Davranışsal
- **Dil:** Türkçe veya İngilizce
- **Zorluk Seviyesi:** Başlangıç, Orta, İleri

Seçim yapıldığında `POST /api/sessions` çağrılır ve yeni bir oturum oluşturulur. Backend, seçilen alana göre soru havuzundan sorular atar ve session ID döndürür.

## Aşama 2: Kalibrasyon (5 saniye)

InterviewSession sayfası açıldığında kamera ve mikrofon izni istenir. Kullanıcıdan düz oturması ve kameraya bakması istenir. Bu 5 saniyelik sürede:

- MediaPipe modelleri yüklenir (Face Mesh + Pose)
- Kalibrasyon referans değerleri hesaplanır (kafa pozisyonu, omuz hizası)
- Ses seviyesi baseline ölçülür
- ASR bağlantısı kurulur

Kalibrasyon tamamlanınca ilk soru gösterilir.

## Aşama 3: Soru-Cevap Döngüsü

Her soru için şu süreç işler:

### Kullanıcı Tarafı
1. Soru ekranda gösterilir
2. Kullanıcı "Start Recording" butonuna basar
3. Kamera ve mikrofon aktif olur, MediaRecorder başlar
4. Kullanıcı soruyu cevaplar
5. "Kaydı Durdur" veya "Next Question" ile geçer

### Arka Plan İşlemleri (Kayıt Sırasında)

**Her 500ms'de bir (Vision):**
- MediaPipe kameradan frame alır
- Face Mesh ile 468 yüz noktası tespit edilir
- Pose ile 33 vücut noktası tespit edilir
- Hesaplanan metrikler:
  - Göz teması skoru (gaze direction)
  - Göz açıklığı (eye openness)
  - Duruş skoru (omuz dengesi + gövde hizası)
  - Kafa titremesi (head jitter)
  - Fidget skoru (kıpırdanma)
  - Kafa yönelimi (yaw, pitch, roll)

**Her 2 saniyede bir (Metrik Batch):**
- Biriken vision metrikleri backend'e gönderilir
- `POST /api/sessions/{id}/events/batch`

**Sürekli (Ses):**
- Mikrofon sesi Speech Service'e akar (WebSocket)
- Whisper partial/final transkript döndürür
- Final segmentler backend'e gönderilir
- `POST /api/sessions/{id}/transcript/batch`

### Soru Geçişi

Kullanıcı sonraki soruya geçtiğinde:
1. MediaRecorder durur, video blob oluşur
2. Blob backend'e upload edilir (soru bazlı kayıt)
3. Kalan metrik ve transkript batch'leri gönderilir
4. Sonraki soru yüklenir, kayıt tekrar başlar

## Aşama 4: Finalize

Son sorudan sonra "Finish" butonuna basıldığında:

```
POST /api/sessions/{sessionId}/finalize
```

Backend finalize işlemi:

1. **Tüm metrikleri topla:** MetricEvents tablosundan oturum boyunca biriken tüm vision metriklerini al
2. **Transkripti birleştir:** TranscriptSegments tablosundan tüm segmentleri zaman sırasına göre birleştir
3. **ScoreCard hesapla:**
   - Göz teması: Kalibrasyon referansına göre normalize et
   - Konuşma hızı: WPM hesapla, ideal aralıkla karşılaştır
   - Dolgu kelime: Filler count / dakika oranını hesapla
   - Duruş: Ortalama posture skorunu normalize et
   - Genel skor: Profil ağırlıklarına göre ağırlıklı ortalama
4. **Pattern detection:** En kötü pencereler, tekrar eden davranış kalıpları
5. **FeedbackItems oluştur:** Tespit edilen pattern'lerden feedback kartları üret
6. **Sonucu kaydet:** ScoreCard ve FeedbackItems veritabanına yazılır

## Aşama 5: Rapor

Finalize tamamlandığında kullanıcı otomatik olarak rapor sayfasına yönlendirilir.

Rapor sayfasında görüntülenen bilgiler:
- Genel skor ve harf notu (A-F)
- Metrik bazlı dağılım kartları
- Soru bazlı video kayıtları (izlenebilir)
- Tam transkript
- AI coaching (Claude ile yetkinlik analizi)
- Dışa aktarma seçenekleri (JSON, Markdown, Print)

Detaylar için bkz. [06-RAPOR-SISTEMI.md](06-RAPOR-SISTEMI.md)

## Ekran Bileşenleri

### InterviewSession Sayfası Düzeni

```
┌────────────────────────────────────────────┐
│  Soru 3 / 10        ⏱ 24:13               │
│  "Mikroservis mimarisinde tutarlılığı..."  │
├──────────────────────┬─────────────────────┤
│                      │  ┌───────────────┐  │
│   📹 Kamera          │  │ Göz Teması %  │  │
│   (VideoCanvas)      │  │ Duruş Skoru % │  │
│                      │  │ Ses Hızı WPM  │  │
│                      │  │ Fidget Oranı  │  │
│                      │  └───────────────┘  │
├──────────────────────┴─────────────────────┤
│  [🔴 Kaydı Durdur]  [Overlay On/Off]      │
│  [Face details]  [Body details]  [Stats]   │
├────────────────────────────────────────────┤
│  [◀ Prev]              [Next Question ▶]  │
└────────────────────────────────────────────┘
```

## Dikkat Edilecekler

- Kamera izni reddedilirse oturum başlatılamaz
- Mikrofon olmadan da oturum başlatılabilir ama transkript üretilemez
- Ağ kopuşlarında metrik batch'leri local buffer'da tutulur ve yeniden gönderilir
- Uzun cevaplarda video blob boyutu büyüyebilir; sıkıştırma yapılır
