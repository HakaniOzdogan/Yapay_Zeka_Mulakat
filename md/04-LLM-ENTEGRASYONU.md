# 04 — LLM Entegrasyonu (Claude + Ollama)

## Genel Bakış

Proje, çift katmanlı LLM mimarisi kullanır. Birincil provider olarak Anthropic Claude API, yedek (fallback) olarak yerel Ollama çalışır. Bu yapı hem kalite hem de erişilebilirlik açısından en iyi dengeyi sağlar.

```
Coaching İsteği
      │
      ▼
┌─────────────────┐
│ LLM Orchestrator │
└───────┬─────────┘
        │
        ▼
┌─────────────────┐     Başarısız?     ┌─────────────────┐
│  Claude API     │ ──────────────────▶│  Ollama (Local) │
│  (Primary)      │                    │  (Fallback)     │
│                 │                    │                 │
│  claude-sonnet  │                    │  qwen2.5:7b     │
│  claude-opus    │                    │                 │
└─────────────────┘                    └─────────────────┘
```

## Neden Claude?

### Kalite Karşılaştırması

| Kriter | Claude (Sonnet/Opus) | GPT-4o | Ollama (Qwen 7B) |
|--------|---------------------|--------|-------------------|
| Türkçe coaching kalitesi | Çok yüksek | Yüksek | Orta |
| Uzun transcript anlama | Mükemmel | İyi | Sınırlı (context) |
| Structured JSON çıktı | Güvenilir | Güvenilir | Ara sıra hatalı |
| Yetkinlik analizi derinliği | Derin ve spesifik | İyi | Yüzeysel |
| Maliyet/çağrı | ~$0.02 (Sonnet) | ~$0.03 | Ücretsiz |

### Model Seçenekleri

**claude-sonnet-4-6** — Önerilen varsayılan. Hız ve kalite dengesi mükemmel. Coaching için yeterli derinlik, düşük maliyet.

**claude-opus-4-6** — Maksimum kalite. Uzun ve karmaşık transkriptlerde belirgin fark. Maliyeti Sonnet'in 5 katı ama kalite farkı buna değer.

## Anthropic API Entegrasyonu

### Mevcut Durum

Mevcut kodda `OpenAiResponsesClient` sınıfı `/v1/chat/completions` endpoint'ini kullanıyor. Claude API farklı bir format kullandığı için yeni bir client yazılması gerekiyor.

### Yeni Client: AnthropicClient

```csharp
// Anthropic API format:
// POST https://api.anthropic.com/v1/messages
// Headers:
//   x-api-key: sk-ant-api03-...
//   anthropic-version: 2023-06-01
//   content-type: application/json
//
// Body:
// {
//   "model": "claude-sonnet-4-6",
//   "max_tokens": 4096,
//   "system": "System prompt here",
//   "messages": [
//     {"role": "user", "content": "User prompt here"}
//   ]
// }
//
// Response:
// {
//   "content": [
//     {"type": "text", "text": "JSON response here"}
//   ],
//   "model": "claude-sonnet-4-6",
//   "usage": {"input_tokens": 1200, "output_tokens": 800}
// }
```

### Konfigürasyon

`appsettings.json` dosyasındaki `Llm` bölümü:

```json
{
  "Llm": {
    "Provider": "Anthropic",
    "ApiKey": "",
    "BaseUrl": "https://api.anthropic.com",
    "PrimaryModel": "claude-sonnet-4-6",
    "ReasoningEffort": "high",
    "FallbackModels": ["qwen2.5:7b-instruct"],
    "TimeoutSeconds": 120,
    "Temperature": 0.2,
    "Retry": {
      "MaxAttemptsPrimary": 2,
      "RetryOnInvalidJson": true,
      "RetryOnTimeout": true,
      "RetryOnHttp5xx": true,
      "BackoffMs": [500, 1000]
    },
    "Fallback": {
      "Enabled": true,
      "Provider": "Ollama",
      "BaseUrl": "http://localhost:11434",
      "Model": "qwen2.5:7b-instruct",
      "TryFallbackModelsOnFailure": true,
      "UseCachedSameInputHashIfAllFail": true
    }
  }
}
```

## Fallback Mekanizması

### Akış

```
1. Claude API'ye istek gönder
2. Başarılı mı?
   ├── Evet → JSON parse et → Validate et → Dön
   └── Hayır → Hata türüne bak:
       ├── HTTP 429 (Rate Limit) → BackoffMs bekle → Retry
       ├── HTTP 5xx (Server Error) → Retry
       ├── Timeout → Retry
       ├── Invalid JSON → Fix prompt ile retry
       └── Tüm retry'lar bitti?
           ├── Fallback aktif mi?
           │   ├── Evet → Ollama'ya gönder → Parse → Validate → Dön
           │   └── Hayır → Cache'e bak → Varsa cache'den dön
           └── Hiçbiri yoksa → Hata döndür
```

### Ne Zaman Fallback Devreye Girer?

- Claude API anahtarı geçersiz veya süresi dolmuş
- Anthropic sunucuları erişilemez (HTTP 5xx)
- Rate limit aşılmış ve retry süresi dolmuş
- Network bağlantı hatası

### Ollama Fallback Sınırlamaları

Ollama'da çalışan Qwen 7B modeli Claude'a göre belirgin kalite farkı yaratır. Beklenen farklılıklar:

- Daha kısa ve yüzeysel feedback metinleri
- Türkçe'de ara sıra gramer hataları
- Karmaşık JSON şemasında ara sıra geçersiz çıktı (retry ile düzelir)
- Yetkinlik derinlik analizi daha genel ifadeler içerir

Bu nedenle Ollama sadece acil durum fallback'i olarak kullanılmalı, birincil coaching provider olarak tercih edilmemelidir.

## Coaching Prompt Yapısı

### System Prompt

LLM'e gönderilen system prompt, modelin çıktı formatını ve kurallarını belirler:

- Çıktı kesinlikle valid JSON olmalı, markdown veya düz metin olmamalı
- `rubric`, `overall`, `feedback`, `drills` olmak üzere 4 ana alan döndürülmeli
- Rubric puanları 0-5 arası, overall 0-100 arası
- Feedback kategorileri: vision, audio, content, structure
- Dil: oturum diline göre Türkçe veya İngilizce

### User Prompt

Evidence summary JSON olarak gönderilir. İçeriği:
- Oturum meta bilgisi (süre, dil, rol)
- Vision metrikleri (göz teması ortalaması, duruş skoru, fidget oranı)
- Ses metrikleri (konuşma hızı WPM, dolgu kelime sayısı, duraklama sayısı)
- Transkript dilimleri (en önemli bölümler)
- Pattern tespitleri (en kötü pencereler, tekrar eden sorunlar)

### Yetkinlik Değerlendirmesi (Yeni)

Mevcut coaching prompt'una ek olarak, yetkinlik boyutları da değerlendirilecek:

- **Teknik Doğruluk (technical_correctness):** Verilen cevaplardaki teknik bilgi doğruluğu
- **Derinlik (depth):** Konuya ne kadar derinden girilebildiği
- **Yapı (structure):** Cevapların ne kadar organize verildiği
- **Netlik (clarity):** Anlatımın ne kadar açık ve anlaşılır olduğu
- **Güven (confidence):** Ses tonu, duruş ve ifade bütünlüğü

## Maliyet Tahmini

### Tek Bir Coaching Çağrısı

Ortalama token kullanımı:
- Input: ~3.000-5.000 token (system + evidence summary)
- Output: ~1.500-2.500 token (coaching JSON)

| Model | Input Fiyat | Output Fiyat | Çağrı Başı Maliyet |
|-------|------------|-------------|-------------------|
| claude-sonnet-4-6 | $3/1M token | $15/1M token | ~$0.01-0.03 |
| claude-opus-4-6 | $15/1M token | $75/1M token | ~$0.05-0.12 |

### Aylık Maliyet Senaryoları

| Kullanım | Sonnet | Opus |
|----------|--------|------|
| 50 mülakat/ay | ~$1-2 | ~$4-6 |
| 200 mülakat/ay | ~$3-5 | ~$15-25 |
| 1000 mülakat/ay | ~$15-30 | ~$75-120 |

Anthropic Console'dan spending limit ayarlanarak bütçe kontrolü sağlanabilir.
