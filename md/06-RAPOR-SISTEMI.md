# 06 — Rapor Sistemi ve Yetkinlik Değerlendirmesi

## Genel Bakış

Rapor sistemi, mülakat oturumunun tamamlanmasının ardından kullanıcıya sunulan kapsamlı performans analizidir. Rapor 6 ana bölümden oluşur:

1. **Genel Performans Skoru** — Harf notu ve sayısal değerlendirme
2. **Metrik Dağılımı** — Göz teması, konuşma hızı, dolgu kelime, duruş
3. **Görüşme Kayıtları** — Soru bazlı video kayıtları
4. **Transkript** — Mülakatın tam yazılı deşifresi
5. **AI Yetkinlik Değerlendirmesi** — Claude ile derinlemesine analiz
6. **Dışa Aktarma** — JSON, Markdown, Yazdırma

## Rapor Akışı

```
Mülakat Biter
      │
      ▼
┌─────────────────┐
│ POST /finalize   │ → ScoreCard + FeedbackItems oluşur
└───────┬─────────┘
        │
        ▼
┌─────────────────┐
│ GET /reports/{id}│ → Tüm rapor verisi frontend'e gelir
└───────┬─────────┘
        │
        ▼
┌─────────────────────────────────────────┐
│            RAPOR SAYFASI                │
│                                         │
│  ┌─────────────────────────────────┐   │
│  │ Genel Skor: 82/100 — Grade B   │   │
│  └─────────────────────────────────┘   │
│                                         │
│  ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐     │
│  │Eye  │ │Speed│ │Fill │ │Post │      │
│  │ 85  │ │ 78  │ │ 72  │ │ 90  │      │
│  └─────┘ └─────┘ └─────┘ └─────┘     │
│                                         │
│  ┌─────────────────────────────────┐   │
│  │ 🎙️ Soru 1: [▶ Video Oynat]     │   │
│  │ 🎙️ Soru 2: [▶ Video Oynat]     │   │
│  │ 🎙️ Soru 3: [▶ Video Oynat]     │   │
│  └─────────────────────────────────┘   │
│                                         │
│  ┌─────────────────────────────────┐   │
│  │ 📄 Tam Transkript               │   │
│  │ "Merhaba, ben Hakan. Bu konuda  │   │
│  │  tecrübem şu şekilde..."        │   │
│  └─────────────────────────────────┘   │
│                                         │
│  ┌─────────────────────────────────┐   │
│  │ 🤖 AI Yetkinlik Değerlendirmesi │   │
│  │ [Generate AI Coaching]          │   │
│  └─────────────────────────────────┘   │
└─────────────────────────────────────────┘
```

## Bölüm 1: Genel Performans Skoru

Genel skor (0-100) puanlama profili ağırlıklarına göre hesaplanır. Ayrıca harf notu ve kısa yorum gösterilir.

| Skor Aralığı | Not | Yorum |
|--------------|-----|-------|
| 90-100 | A | Mükemmel Performans |
| 80-89 | B | Çok İyi |
| 70-79 | C | İyi, Gelişime Açık |
| 60-69 | D | Geliştirilmesi Gerekli |
| 0-59 | F | Hedefli Pratik Gerekli |

Skor dairesel bir grafik (conic-gradient) ile görselleştirilir.

## Bölüm 2: Metrik Dağılımı

4 ana metrik kartı:

### Göz Teması (Eye Contact)
Kullanıcının mülakat boyunca ne kadar kameraya baktığını ölçer. MediaPipe Face Mesh'ten elde edilen iris pozisyonuna göre gaze direction hesaplanır. İdeal: %55'in üzeri.

### Konuşma Hızı (Speaking Rate)
Dakikadaki kelime sayısı (WPM). İdeal aralık: 120-170 WPM. Çok hızlı veya çok yavaş konuşma skoru düşürür.

### Dolgu Kelimeler (Filler Words)
"Eee", "şey", "yani", "hani" gibi dolgu kelime ve kalıpların dakikadaki sayısı. İdeal: dakikada 6'dan az.

### Duruş (Posture)
Omuz dengesi, gövde hizası ve genel beden dili stabilitesi. Pose landmark'larından hesaplanır. İdeal: %60'ın üzeri.

## Bölüm 3: Görüşme Kayıtları

Her soru için ayrı bir kart gösterilir:

- **Soru numarası ve metni** — "Soru 1: Mikroservis mimarisinde..."
- **Video oynatıcı** — Kullanıcının o soruya verdiği yanıtın video kaydı
- **Kayıt bulunamadı durumu** — Video upload başarısız olduysa bilgilendirme

Video'lar backend'de sorulara bağlı olarak `audioUrl` alanında saklanır. Frontend `<video>` elementi ile oynatır.

## Bölüm 4: Transkript

Mülakatın tam yazılı deşifresi. Whisper ASR tarafından üretilen transkript segmentleri birleştirilmiş haliyle gösterilir.

Transkript raporda şu formatta sunulur:
- Başlık: "📄 Tam Görüşme Deşifresi"
- İçerik: Düz metin olarak tüm konuşma
- Kaydırılabilir alan içinde

Bu alan daha önce "live transcript" olarak mülakat sırasında anlık gösteriliyordu. Artık **sadece raporda** görüntüleniyor — mülakat sırasında canlı transkript ekranı kaldırıldı. Transkript verisi arka planda toplanmaya devam ediyor ama kullanıcıya gösterilmiyor.

## Bölüm 5: AI Yetkinlik Değerlendirmesi

Bu bölüm, rapordaki en kritik kısımdır. "Generate AI Coaching" butonuna basıldığında Claude API çağrılır.

### Gönderilen Veri (Evidence Summary)

Claude'a şu bilgiler gönderilir:
- Oturum meta bilgisi (süre, dil, seçilen rol/alan)
- Vision metrikleri ortalamaları ve en kötü pencereler
- Ses metrikleri (WPM, filler count, pause count)
- Transkript dilimleri (en anlamlı bölümler)
- Pattern tespitleri (tekrar eden davranış sorunları)

### Dönen Yetkinlik Raporu

Claude şu yapıda JSON döndürür:

#### Rubric (Yetkinlik Matrisi)

| Boyut | Puan | Açıklama |
|-------|------|----------|
| Teknik Doğruluk | 0-5 | Verilen cevaplardaki teknik bilginin doğruluğu |
| Derinlik | 0-5 | Konuya ne kadar detaylı ve kapsamlı girildiği |
| Yapı | 0-5 | Cevapların ne kadar organize ve sistematik olduğu |
| Netlik | 0-5 | Anlatımın açıklığı, anlaşılırlığı |
| Güven | 0-5 | Ses tonu, duruş, göz teması ile yansıyan özgüven |

#### Genel Skor (Overall)

0-100 arası genel yetkinlik puanı. Rubric boyutları ve davranış metriklerinin birleşimi.

#### Feedback (Geri Bildirim) — 5-10 Madde

Her feedback maddesi:
- **Kategori:** vision, audio, content, structure
- **Ciddiyet:** 1-5 (5 en ciddi)
- **Başlık:** Kısa açıklama
- **Kanıt (Evidence):** Transkript veya metriklerden somut örnek
- **Zaman Aralığı:** Sorunun yaşandığı zaman dilimi
- **Öneri (Suggestion):** Nasıl iyileştirilebileceği
- **Örnek İfade:** Daha iyi nasıl söylenebilirdi

#### Drills (Pratik Alıştırmalar)

Her drill:
- **Başlık:** Alıştırmanın adı
- **Adımlar:** Sıralı talimatlar
- **Süre:** Tahmini dakika

### Örnek AI Coaching Çıktısı

```json
{
  "rubric": {
    "technical_correctness": 3,
    "depth": 2,
    "structure": 4,
    "clarity": 3,
    "confidence": 4
  },
  "overall": 68,
  "feedback": [
    {
      "category": "content",
      "severity": 4,
      "title": "Mikroservis Tutarlılık Açıklaması Yüzeysel",
      "evidence": "Aday SAGA pattern'den bahsetti ancak compensating transaction mekanizmasını açıklamadı",
      "time_range_ms": [45000, 78000],
      "suggestion": "SAGA pattern açıklarken choreography vs orchestration farkını ve compensating transaction örneklerini verin",
      "example_phrase": "SAGA pattern'de her servis kendi local transaction'ını commit eder ve başarısız olursa compensating transaction ile geri alır"
    }
  ],
  "drills": [
    {
      "title": "SAGA Pattern Derinleştirme",
      "steps": [
        "Bir e-ticaret sipariş akışı çizin",
        "Her adım için compensating transaction tanımlayın",
        "Choreography ve orchestration yaklaşımlarını karşılaştırın",
        "2 dakikada sesli anlatım yapın"
      ],
      "duration_min": 15
    }
  ]
}
```

## Bölüm 6: Dışa Aktarma

Rapor 3 farklı formatta dışa aktarılabilir:

- **JSON Export:** Tüm rapor verisi yapılandırılmış JSON olarak
- **Markdown Export:** Okunabilir Markdown formatında
- **Print:** Tarayıcının yazdırma dialogu ile PDF'e dönüştürülebilir

## Puanlama Profili Değiştirme

Rapor sayfasında "Scoring Profile" paneli açılarak farklı profiller arasında geçiş yapılabilir:

- **Preview:** Yeni profil ile skorun nasıl değişeceğini önizle
- **Apply:** Profili değiştir
- **Apply + Recalculate:** Profili değiştir ve raporu yeniden hesapla

Bu özellik farklı mülakat türleri (genel, teknik, HR) için farklı ağırlıkların uygulanmasını sağlar.
