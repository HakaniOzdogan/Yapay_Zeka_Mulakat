# 08 — Görüntü Analizi (MediaPipe)

## Genel Bakış

Sistem, Google MediaPipe kütüphanesini kullanarak tarayıcı üzerinde gerçek zamanlı yüz ve vücut analizi yapar. Tüm hesaplama istemci tarafında (client-side) gerçekleşir, sunucuya video stream'i gönderilmez.

## Kullanılan MediaPipe Modülleri

### Face Mesh
468 yüz noktası (landmark) tespit eder. Kullanılan bilgiler:
- İris pozisyonu → göz teması yönü
- Göz kapağı açıklığı → göz openness
- Yüz yönelimi → kafa pitch/yaw/roll

### Pose
33 vücut noktası tespit eder. Kullanılan bilgiler:
- Omuz noktaları → omuz dengesi ve duruş
- Gövde merkez noktası → fidget (kıpırdanma) tespiti

## Hesaplanan Metrikler

### 1. Göz Teması Skoru (Eye Contact Score)

Kullanıcının kameraya bakıp bakmadığını ölçer.

Hesaplama: Face Mesh'ten iris landmark'ları alınır. Iris pozisyonunun göz çerçevesine göre konumu hesaplanır. Kameraya doğrudan bakışa yakın pozisyonlar yüksek skor alır.

Kalibrasyon ile kullanıcının doğal bakış pozisyonu referans alınır. Bu sayede gözlük takan veya farklı kamera açısı olan kullanıcılar da doğru değerlendirilir.

Skor aralığı: 0.0 - 1.0 (normalize edilmiş)
İdeal eşik: 0.55 üzeri

### 2. Göz Açıklığı (Eye Openness)

Göz kapağı aralığını ölçer. Düşük değerler yorgunluk veya ilgisizlik göstergesi olabilir.

Hesaplama: Üst ve alt göz kapağı landmark'ları arasındaki mesafe, göz genişliğine oranlanır.

### 3. Duruş Skoru (Posture Score)

Omuz dengesi ve gövde hizasını ölçer.

Hesaplama: Sol ve sağ omuz landmark'larının Y koordinat farkı omuz dengesini verir. Gövde merkez noktasının horizontal sapması gövde hizasını gösterir. İki değerin ağırlıklı ortalaması posture skorunu oluşturur.

Skor aralığı: 0.0 - 1.0
İdeal eşik: 0.60 üzeri

### 4. Kafa Titremesi (Head Jitter)

Kafanın ne kadar stabil tutulduğunu ölçer. Yüksek jitter stres veya rahatsızlık göstergesi olabilir.

Hesaplama: Ardışık frame'lerdeki kafa pozisyon değişimlerinin standart sapması. Rolling window (5 saniyelik pencere) üzerinden hesaplanır.

İdeal eşik: 0.35 altı

### 5. Fidget Skoru (Kıpırdanma)

Vücut hareketliliğini ölçer. Çok fazla kıpırdanma dikkat dağınıklığı veya gerginlik göstergesi olabilir.

Hesaplama: Pose landmark'larının frame'ler arası toplam hareket miktarı. Normalize edilmiş değer.

İdeal eşik: 0.40 altı

### 6. Kafa Yönelimi (Head Pose)

Kafanın 3D açılarını ölçer: yaw (sağ-sol), pitch (yukarı-aşağı), roll (eğilme).

Kameradan uzaklaşan yönelimler göz teması ve ilgi düzeyini etkiler.

## İşlem Akışı

```
Kamera Frame (30 FPS)
        │
        ▼
   MediaPipe
   Face Mesh + Pose
        │
        ▼
   Ham Landmark'lar
        │
        ▼
  ┌─────────────────────────────────────────┐
  │         features.ts                      │
  │  computeEyeContactScore()               │
  │  computeEyeOpenness()                    │
  │  computePostureScore()                   │
  │  computeHeadJitter()                     │
  │  computeFidgetScore()                    │
  │  computeHeadPose()                       │
  └───────────┬─────────────────────────────┘
              │
              ▼
  ┌─────────────────────────────────────────┐
  │    MetricsComputer.ts                    │
  │  Rolling buffer (5 sn)                   │
  │  Exponential smoothing (α=0.2)           │
  │  Kalibrasyon normalize                   │
  └───────────┬─────────────────────────────┘
              │
              ▼
  ┌─────────────────────────────────────────┐
  │    SessionTransport.ts                   │
  │  Her 500ms batch olarak backend'e gönder │
  │  POST /events/batch                      │
  └─────────────────────────────────────────┘
```

## Kalibrasyon

Mülakat başında 5 saniyelik kalibrasyon yapılır. Bu sürede:

- Kullanıcıdan düz oturması ve kameraya bakması istenir
- Ortalama kafa pozisyonu referans olarak kaydedilir
- Ortalama omuz pozisyonu referans olarak kaydedilir
- Göz teması baseline ölçülür

Kalibrasyon referans değerleri sonraki tüm metriklerin normalize edilmesinde kullanılır. Bu sayede farklı kamera açıları ve oturuş pozisyonları doğru şekilde değerlendirilir.

## Overlay Görselleştirme

Mülakat sırasında opsiyonel olarak overlay açılabilir:

- **Face details:** Yüz mesh çizgileri ve iris noktaları
- **Body details:** Pose landmark'ları ve kemik çizgileri
- **Live stats:** Anlık metrik değerleri

Overlay geliştirme ve debug amaçlıdır. Gerçek mülakat sırasında kapalı tutulması önerilir.

## Backend'e Gönderilen Veri

```json
{
  "clientEventId": "uuid",
  "tsMs": 4500,
  "source": "Vision",
  "type": "vision_metrics_v1",
  "payload": {
    "eyeContact": 0.82,
    "posture": 0.74,
    "fidget": 0.21,
    "headJitter": 0.18,
    "eyeOpenness": 0.65,
    "calibrated": true
  }
}
```

## Performans

- MediaPipe WASM backend: ~15-25ms per frame
- GPU backend (WebGL): ~5-10ms per frame
- Metrik hesaplama: <1ms
- Toplam budget: 30 FPS hedefi için frame başına 33ms
- Rolling buffer: 5 saniye * 30 FPS = 150 frame saklanır
