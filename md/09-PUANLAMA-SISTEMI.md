# 09 — Puanlama Sistemi

## Genel Bakış

Puanlama sistemi, mülakat oturumu sonunda toplanan metrikleri konfigüre edilebilir ağırlık ve eşik değerlerine göre 0-100 arası skorlara dönüştürür. Farklı mülakat türleri için farklı puanlama profilleri tanımlanabilir.

## Puanlama Profilleri

Sistem 3 yerleşik profil ile gelir. Yeni profiller `appsettings.json` üzerinden eklenebilir.

### Genel (General) Profil

Tüm metriklere eşit yakın ağırlık verir. Genel amaçlı mülakatlar için varsayılan profil.

| Metrik | Ağırlık |
|--------|---------|
| Göz Teması | %25 |
| Konuşma Hızı | %25 |
| Dolgu Kelimeler | %25 |
| Duruş | %15 |
| Fidget | %10 |

Eşik değerleri:
- Konuşma hızı ideal aralık: 120-170 WPM
- Dolgu kelime maksimum: dakikada 6
- Göz teması minimum: %55
- Kafa titremesi maksimum: 0.35
- Fidget maksimum: 0.40
- Duruş minimum: %60

### Teknik Profil

İçerik ve konuşma kalitesine daha fazla ağırlık verir. Teknik mülakatlarda beden dili daha az önemlidir.

| Metrik | Ağırlık |
|--------|---------|
| Göz Teması | %15 |
| Konuşma Hızı | %30 |
| Dolgu Kelimeler | %35 |
| Duruş | %10 |
| Fidget | %10 |

### HR / Davranışsal Profil

Beden dili ve göz temasına daha fazla ağırlık verir. HR mülakatlarında iletişim ve güven daha kritiktir.

| Metrik | Ağırlık |
|--------|---------|
| Göz Teması | %30 |
| Konuşma Hızı | %15 |
| Dolgu Kelimeler | %20 |
| Duruş | %20 |
| Fidget | %15 |

## Skor Hesaplama Algoritması

Her metrik kendi eşik değerlerine göre 0-100 arası bir skora dönüştürülür, ardından profil ağırlıklarıyla çarpılarak genel skor elde edilir.

### Göz Teması Skoru

```
eyeContactAvg = oturum boyunca göz teması ortalaması (0.0-1.0)
threshold = profilden eyeContactMin (örn: 0.55)

if eyeContactAvg >= threshold:
    score = 70 + (eyeContactAvg - threshold) / (1.0 - threshold) * 30
else:
    score = (eyeContactAvg / threshold) * 70
```

### Konuşma Hızı Skoru

```
wpm = toplam kelime / toplam dakika
idealMin = profilden speakingRateIdealMinWpm
idealMax = profilden speakingRateIdealMaxWpm

if idealMin <= wpm <= idealMax:
    score = 100  # İdeal aralıkta
elif wpm < idealMin:
    score = max(0, 100 - (idealMin - wpm) * 2)
else:
    score = max(0, 100 - (wpm - idealMax) * 2)
```

### Dolgu Kelime Skoru

```
fillerPerMin = toplam filler count / toplam dakika
maxFiller = profilden fillerPerMinMax

if fillerPerMin <= 1:
    score = 100
elif fillerPerMin >= maxFiller * 2:
    score = 0
else:
    score = max(0, 100 - (fillerPerMin / maxFiller) * 60)
```

### Duruş Skoru

```
postureAvg = oturum boyunca posture ortalaması (0.0-1.0)
threshold = profilden postureMin

Göz teması ile aynı formül uygulanır.
```

### Genel Skor

```
overall = eyeContactScore * w_eye
        + speakingRateScore * w_speaking
        + fillerScore * w_filler
        + postureScore * w_posture
        + fidgetScore * w_fidget

// Sonuç 0-100 arası clamp edilir
```

## Profil Değiştirme ve Yeniden Hesaplama

Rapor sayfasında kullanıcı profil değiştirebilir:

- **Preview:** Seçilen profil ile yeni skorları ön izle (veritabanı değişmez)
- **Apply:** Sadece profili değiştir
- **Apply + Recalculate:** Profili değiştir ve finalize'ı yeniden çalıştır

Bu özellik aynı mülakatı farklı kriterlere göre değerlendirmeyi sağlar.

## Konfigürasyon

`appsettings.json` dosyasındaki `ScoringProfiles` bölümü:

```json
{
  "ScoringProfiles": {
    "DefaultProfile": "general",
    "Profiles": {
      "general": {
        "weights": {
          "eyeContact": 0.25,
          "posture": 0.15,
          "fidget": 0.10,
          "speakingRate": 0.25,
          "fillerWords": 0.25
        },
        "thresholds": {
          "speakingRateIdealMinWpm": 120,
          "speakingRateIdealMaxWpm": 170,
          "fillerPerMinMax": 6,
          "eyeContactMin": 0.55,
          "headJitterMax": 0.35,
          "fidgetMax": 0.40,
          "postureMin": 0.60
        }
      }
    }
  }
}
```

Yeni profil eklemek için `Profiles` altına yeni bir key eklemek yeterlidir.
