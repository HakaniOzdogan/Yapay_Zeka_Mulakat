# 07 — Transkript Sistemi

## Genel Bakış

Transkript sistemi, kullanıcının mülakat sırasındaki konuşmasını otomatik olarak yazıya döker. Sistem iki modda çalışır: arka plan transkript toplama (mülakat sırasında) ve tam transkript gösterimi (raporda).

## Mimari

```
Mikrofon → AudioAnalyzer → WebSocket → Speech Service → Whisper ASR
                                              │
                                    ┌─────────┴──────────┐
                                    │ Partial (anlık)     │  → Arka planda birikir
                                    │ Final (kesinleşmiş) │  → Backend'e gönderilir
                                    └────────────────────┘
                                              │
                                    POST /transcript/batch
                                              │
                                         PostgreSQL
                                     TranscriptSegments
                                              │
                                    GET /reports/{id}
                                              │
                                      Raporda gösterilir
```

## Canlı Transkript Kaldırıldı

Önceki sürümde `LiveTranscript.tsx` bileşeni mülakat sırasında anlık transkripti ekranda gösteriyordu. Bu özellik kaldırıldı çünkü:

- Kullanıcının dikkatini dağıtıyordu — ekranda kayan metin yerine kameraya odaklanması gerekir
- Partial (kesinleşmemiş) transkriptler yanıltıcı olabiliyordu
- Ek render yükü performansı etkiliyordu

Arka plandaki veri toplama aynen devam ediyor. Transkript verisi mülakat boyunca Speech Service üzerinden toplanır, backend'e batch olarak gönderilir ve veritabanında saklanır. Tek fark: kullanıcıya mülakat sırasında gösterilmemesi.

## Speech Service Detayı

### Whisper ASR

Speech Service, OpenAI Whisper modelini FastAPI ile sarmalayan bir Python servisidir.

Konum: `services/speech-service/`

Desteklenen modlar:

**Streaming ASR (WebSocket):**
- Mikrofon sesini anlık olarak işler
- Partial results: henüz kesinleşmemiş metin
- Final results: kelime sınırlarında kesinleşen metin
- Bağlantı kopuşlarında otomatik yeniden bağlanma (5 saniye)

**Batch Transcription (HTTP):**
- Tamamlanmış ses dosyasını alır, tam transkript döndürür
- Soru geçişlerinde kullanılır
- Segment bazlı zaman damgaları içerir

### Desteklenen Diller

- Türkçe (tr) — varsayılan
- İngilizce (en)

Dil seçimi mülakat başlangıcında yapılır ve Speech Service'e iletilir.

### Çıktı Formatı

```json
{
  "segments": [
    {
      "start": 1.2,
      "end": 3.6,
      "text": "Merhaba, ben bu konuda tecrübeliyim.",
      "confidence": 0.93
    }
  ],
  "full_text": "Merhaba, ben bu konuda tecrübeliyim.",
  "duration_ms": 3600,
  "word_count": 6,
  "wpm": 100,
  "filler_count": 0,
  "pause_count": 1
}
```

## Backend Transkript İşleme

### Batch Ingestion

Frontend topladığı transkript segmentlerini batch olarak gönderir:

```
POST /api/sessions/{sessionId}/transcript/batch

[
  {
    "clientSegmentId": "unique-id",
    "startMs": 1200,
    "endMs": 3600,
    "text": "Merhaba, ben bu konuda tecrübeliyim.",
    "confidence": 0.93
  }
]
```

### Duplicate Prevention

Aynı segment birden fazla kez gönderilebilir (ağ tekrarı). Backend, `clientSegmentId` bazlı deduplication yapar. Ayrıca normalize edilmiş metin karşılaştırması ile ardışık duplikat segmentler filtrelenir.

### Finalize Sırasında

Oturum finalize edildiğinde tüm segmentler zaman sırasına göre birleştirilir ve `full_text` olarak rapor verisine dahil edilir.

## Transkriptin Raporda Gösterimi

Rapor sayfasında "Tam Görüşme Deşifresi" başlığı altında tek bir blok metin olarak sunulur. Kullanıcı bu bölümü okuyarak mülakat sırasında ne söylediğini görebilir.

## Transkript ve AI Coaching İlişkisi

Transkript, AI coaching çağrısında evidence summary'nin en önemli parçasıdır. Claude, transkriptten:

- Teknik doğruluğu değerlendirir (ne söylendi?)
- Yapı ve organizasyonu analiz eder (nasıl söylendi?)
- Dolgu kelime ve duraklamaları tespit eder
- Spesifik örneklerle feedback oluşturur
- Alternatif ifade önerileri sunar

Transkript yoksa veya çok kısa ise, Claude vision metrikleri ve pattern tespitlerine dayalı daha genel bir coaching üretir.

## Gizlilik

Transkriptler hassas veri içerebilir. Konfigürasyondaki gizlilik ayarları:

- `Privacy.RedactTranscripts`: PII (kişisel bilgi) redaction'ı aktif/pasif
- `Privacy.RedactOnIngest`: Transkript geldiği anda redakte et
- `Privacy.StoreOriginalTranscripts`: Orijinal metni sakla/saklama
- `Retention.DeleteAfterDays`: 30 gün sonra otomatik sil
