# MediaPipe Kapsamlı Yüz Analizi Mimarisi

> **Kapsam:** Bu döküman, MediaPipe FaceLandmarker kullanarak mülakat platformuna entegre edilebilecek en kapsamlı yüz analizi sistemini tanımlar. Tüm analizler gözlemlenebilir davranışsal sinyallere dayanır; yorumlar kullanıcıya gelişim aracı olarak sunulur.

---

## İçindekiler

1. [Sistem Mimarisi](#1-sistem-mimarisi)
2. [MediaPipe Kurulumu ve Konfigürasyonu](#2-mediapipe-kurulumu-ve-konfigürasyonu)
3. [Sinyal Katmanları](#3-sinyal-katmanları)
   - 3.1 [Göz Analizi](#31-göz-analizi)
   - 3.2 [Baş Pozu Analizi](#32-baş-pozu-analizi)
   - 3.3 [52 Blendshape Analizi](#33-52-blendshape-analizi)
   - 3.4 [Bölgesel Yüz Analizi](#34-bölgesel-yüz-analizi)
4. [Zaman Serisi ve Pencere Sistemi](#4-zaman-serisi-ve-pencere-sistemi)
5. [Sinyal Birleştirme ve Kompozit Skorlar](#5-sinyal-birleştirme-ve-kompozit-skorlar)
6. [Web Worker Mimarisi](#6-web-worker-mimarisi)
7. [Python Backend Analizi](#7-python-backend-analizi)
8. [Veri Modelleri (TypeScript)](#8-veri-modelleri-typescript)
9. [Rapor Üretimi](#9-rapor-üretimi)
10. [Landmark Referans Tablosu](#10-landmark-referans-tablosu)

---

## 1. Sistem Mimarisi

```
┌─────────────────────────────────────────────────────────────────┐
│                        TARAYICI (Main Thread)                    │
│                                                                  │
│  ┌──────────────┐    ┌───────────────┐    ┌──────────────────┐  │
│  │  Webcam Feed │───▶│  Canvas/Video │───▶│  UI Overlay      │  │
│  └──────────────┘    └───────────────┘    └──────────────────┘  │
│          │                                                        │
│          ▼                                                        │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              Web Worker (Analysis Thread)                 │   │
│  │                                                           │   │
│  │  ┌─────────────────┐    ┌──────────────────────────┐    │   │
│  │  │ MediaPipe        │───▶│  Signal Extractor        │    │   │
│  │  │ FaceLandmarker   │    │  - EAR / LAR / MAR       │    │   │
│  │  │ (478 landmarks + │    │  - Head Pose (P/Y/R)     │    │   │
│  │  │  52 blendshapes +│    │  - 52 Blendshapes        │    │   │
│  │  │  transform matrix│    │  - Iris Gaze Vector      │    │   │
│  │  └─────────────────┘    └──────────────────────────┘    │   │
│  │                                    │                      │   │
│  │                          ┌─────────▼──────────┐          │   │
│  │                          │  Time Window Buffer │          │   │
│  │                          │  (sliding 30s / 5s) │          │   │
│  │                          └─────────┬──────────┘          │   │
│  │                                    │                      │   │
│  │                          ┌─────────▼──────────┐          │   │
│  │                          │  Composite Scorer   │          │   │
│  │                          │  - Arousal Index    │          │   │
│  │                          │  - Attention Score  │          │   │
│  │                          │  - Authenticity     │          │   │
│  │                          │  - Stability Score  │          │   │
│  │                          └─────────┬──────────┘          │   │
│  └────────────────────────────────────┼──────────────────────┘  │
│                                       │ postMessage              │
│                                       ▼                          │
│                          ┌────────────────────────┐             │
│                          │  Real-time UI Updates  │             │
│                          └────────────┬───────────┘             │
└───────────────────────────────────────┼─────────────────────────┘
                                        │ Socket.io / REST
                                        ▼
┌─────────────────────────────────────────────────────────────────┐
│                    NEXT.JS API / PYTHON BACKEND                  │
│                                                                  │
│  ┌────────────────────┐    ┌──────────────────────────────────┐ │
│  │  Frame Buffer Store│    │  Post-Interview Batch Analyzer   │ │
│  │  (per question)    │    │  - Temporal pattern mining       │ │
│  └────────────────────┘    │  - Cross-signal correlation      │ │
│                             │  - Report generation            │ │
│                             └──────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

---

## 2. MediaPipe Kurulumu ve Konfigürasyonu

### Paket Kurulumu

```bash
npm install @mediapipe/tasks-vision
```

### FaceLandmarker Başlatma

```typescript
// lib/mediapipe/faceLandmarker.ts

import {
  FaceLandmarker,
  FilesetResolver,
  FaceLandmarkerResult,
  NormalizedLandmark,
} from "@mediapipe/tasks-vision";

export class FaceLandmarkerService {
  private faceLandmarker: FaceLandmarker | null = null;
  private runningMode: "IMAGE" | "VIDEO" = "VIDEO";

  async initialize(): Promise<void> {
    const filesetResolver = await FilesetResolver.forVisionTasks(
      "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@latest/wasm"
    );

    this.faceLandmarker = await FaceLandmarker.createFromOptions(
      filesetResolver,
      {
        baseOptions: {
          modelAssetPath:
            "https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/1/face_landmarker.task",
          delegate: "GPU", // GPU tercih et, fallback CPU
        },
        runningMode: this.runningMode,
        numFaces: 1,
        minFaceDetectionConfidence: 0.5,
        minFacePresenceConfidence: 0.5,
        minTrackingConfidence: 0.5,

        // KRİTİK: Bu iki seçeneği MUTLAKA aç
        outputFaceBlendshapes: true,        // 52 blendshape katsayıları
        outputFacialTransformationMatrixes: true, // 4x4 transform matrix (baş pozu)
      }
    );
  }

  detectForVideo(
    video: HTMLVideoElement,
    timestamp: number
  ): FaceLandmarkerResult | null {
    if (!this.faceLandmarker) return null;
    return this.faceLandmarker.detectForVideo(video, timestamp);
  }

  close(): void {
    this.faceLandmarker?.close();
  }
}
```

### Model Çıktı Yapısı

```typescript
// MediaPipe FaceLandmarkerResult yapısı
interface FaceLandmarkerResult {
  // 478 adet 3D landmark (x, y, z normalize edilmiş 0-1)
  faceLandmarks: NormalizedLandmark[][];

  // 52 blendshape skoru (her biri 0.0 - 1.0)
  faceBlendshapes: Classifications[];

  // 4x4 dönüşüm matrisi (baş pozu için)
  facialTransformationMatrixes: Matrix4x4[];
}
```

---

## 3. Sinyal Katmanları

### 3.1 Göz Analizi

#### 3.1.1 EAR — Eye Aspect Ratio (Göz Kırpma)

Göz kırpma frekansı stres ve bilişsel yük ile doğrudan ilişkilidir. Dakikada 15-20 kırpma normaldir; bu değerin üstü yorgunluk/stres, altı ise aşırı konsantrasyon veya donup kalma işaretidir.

```typescript
// lib/mediapipe/analyzers/eyeAnalyzer.ts

// MediaPipe Face Mesh landmark indeksleri
const LEFT_EYE_INDICES = {
  top: [159, 160, 161],       // Üst kapak
  bottom: [145, 144, 163],    // Alt kapak
  left: 33,                   // Sol köşe
  right: 133,                 // Sağ köşe
};

const RIGHT_EYE_INDICES = {
  top: [386, 385, 384],
  bottom: [374, 373, 390],
  left: 362,
  right: 263,
};

// İris landmark indeksleri (478 landmark modelde 468-477 arası)
const LEFT_IRIS_INDICES  = [468, 469, 470, 471, 472]; // merkez + 4 kenar
const RIGHT_IRIS_INDICES = [473, 474, 475, 476, 477];

function euclidean(
  a: NormalizedLandmark,
  b: NormalizedLandmark
): number {
  return Math.sqrt(
    Math.pow(a.x - b.x, 2) +
    Math.pow(a.y - b.y, 2) +
    Math.pow(a.z - b.z, 2)
  );
}

export function calculateEAR(
  landmarks: NormalizedLandmark[],
  eye: "left" | "right"
): number {
  const idx = eye === "left" ? LEFT_EYE_INDICES : RIGHT_EYE_INDICES;

  // Dikey mesafeler (3 nokta ortalaması)
  const v1 = euclidean(landmarks[idx.top[0]], landmarks[idx.bottom[0]]);
  const v2 = euclidean(landmarks[idx.top[1]], landmarks[idx.bottom[1]]);
  const v3 = euclidean(landmarks[idx.top[2]], landmarks[idx.bottom[2]]);

  // Yatay mesafe
  const h = euclidean(landmarks[idx.left], landmarks[idx.right]);

  // EAR formülü (Soukupova & Cech, 2016)
  return (v1 + v2 + v3) / (3.0 * h);
}

export function detectBlink(ear: number, threshold = 0.20): boolean {
  return ear < threshold;
}

// Her iki göz ortalaması
export function calculateMeanEAR(landmarks: NormalizedLandmark[]): number {
  const leftEAR  = calculateEAR(landmarks, "left");
  const rightEAR = calculateEAR(landmarks, "right");
  return (leftEAR + rightEAR) / 2;
}
```

#### 3.1.2 Göz Teması ve Gaze Vektörü

```typescript
// lib/mediapipe/analyzers/gazeAnalyzer.ts

export interface GazeResult {
  irisLeftCenter:  { x: number; y: number; z: number };
  irisRightCenter: { x: number; y: number; z: number };
  gazeVector:      { x: number; y: number; z: number };
  isLookingAtCamera: boolean;
  deviationAngle: number; // Derece cinsinden sapma
  eyeContactScore: number; // 0-1
}

export function analyzeGaze(
  landmarks: NormalizedLandmark[]
): GazeResult {
  // İris merkezlerini hesapla (5 nokta ortalaması)
  const leftCenter  = getIrisCenter(landmarks, LEFT_IRIS_INDICES);
  const rightCenter = getIrisCenter(landmarks, RIGHT_IRIS_INDICES);

  // İki iris ortası = gaze origin
  const gazeOrigin = {
    x: (leftCenter.x + rightCenter.x) / 2,
    y: (leftCenter.y + rightCenter.y) / 2,
    z: (leftCenter.z + rightCenter.z) / 2,
  };

  // Kamera merkezi (0.5, 0.5) — normalize koordinat sistemi
  const CAMERA_CENTER = { x: 0.5, y: 0.5 };

  // 2D düzlemde sapma
  const dx = gazeOrigin.x - CAMERA_CENTER.x;
  const dy = gazeOrigin.y - CAMERA_CENTER.y;
  const deviation = Math.sqrt(dx * dx + dy * dy);

  // Açıya çevir (yaklaşık)
  const deviationAngle = Math.atan2(deviation, 0.5) * (180 / Math.PI);

  // 12 derecenin altı = kameraya bakıyor (pratik eşik)
  const isLookingAtCamera = deviationAngle < 12;

  // Smooth score (1 = tam kameraya, 0 = tamamen başka yöne)
  const eyeContactScore = Math.max(0, 1 - deviationAngle / 45);

  return {
    irisLeftCenter:    leftCenter,
    irisRightCenter:   rightCenter,
    gazeVector:        gazeOrigin,
    isLookingAtCamera,
    deviationAngle,
    eyeContactScore,
  };
}

function getIrisCenter(
  landmarks: NormalizedLandmark[],
  indices: number[]
): { x: number; y: number; z: number } {
  let x = 0, y = 0, z = 0;
  indices.forEach((i) => {
    x += landmarks[i].x;
    y += landmarks[i].y;
    z += landmarks[i].z;
  });
  return { x: x / indices.length, y: y / indices.length, z: z / indices.length };
}
```

#### 3.1.3 Kamera Mesafesi (İris Çapı Yöntemi)

```typescript
// İnsan irisinin gerçek çapı sabit: ~11.7 mm
// Bu özellik ile kameraya mesafe hesaplanabilir
export function estimateCameraDistance(
  landmarks: NormalizedLandmark[],
  videoWidth: number,
  videoHeight: number
): number {
  const leftIris  = getIrisCenter(landmarks, LEFT_IRIS_INDICES);
  const rightIris = getIrisCenter(landmarks, RIGHT_IRIS_INDICES);

  // İris çapı: sol irisin en geniş iki noktası arası mesafe (px cinsinden)
  const leftEdge1 = landmarks[469]; // Sol iris sol kenar
  const leftEdge2 = landmarks[471]; // Sol iris sağ kenar

  const irisDiameterPx = Math.sqrt(
    Math.pow((leftEdge1.x - leftEdge2.x) * videoWidth, 2) +
    Math.pow((leftEdge1.y - leftEdge2.y) * videoHeight, 2)
  );

  // Basit pinhole kamera modeli
  const REAL_IRIS_DIAMETER_MM = 11.7;
  const FOCAL_LENGTH_PX = videoWidth * 1.0; // Tahmini focal length

  const distanceMm =
    (REAL_IRIS_DIAMETER_MM * FOCAL_LENGTH_PX) / irisDiameterPx;

  return distanceMm / 10; // cm cinsinden
}
```

---

### 3.2 Baş Pozu Analizi

MediaPipe `facialTransformationMatrixes` çıktısı, 4×4 bir dönüşüm matrisi verir. Bu matris, Euler açılarına ayrıştırılarak Pitch/Yaw/Roll elde edilir.

```typescript
// lib/mediapipe/analyzers/headPoseAnalyzer.ts

export interface HeadPoseResult {
  pitch: number; // Yukarı(+) / Aşağı(-) — derece
  yaw:   number; // Sola(+) / Sağa(-) — derece
  roll:  number; // Saat yönü(+) / Ters(-) — derece
  isLookingAway:  boolean; // |yaw| > 20° veya |pitch| > 15°
  headStability:  number;  // 0-1: 1 = tamamen sabit
  headMovementRate: number; // Derece/saniye
}

export function extractHeadPose(
  transformMatrix: number[]
): { pitch: number; yaw: number; roll: number } {
  // 4x4 matris row-major order: [r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz, 0,0,0,1]
  const r = transformMatrix;

  // Rotasyon matrisinden Euler açıları (ZYX sırası)
  const sy = Math.sqrt(r[0] * r[0] + r[4] * r[4]);
  const singular = sy < 1e-6;

  let pitch: number, yaw: number, roll: number;

  if (!singular) {
    pitch = Math.atan2(r[9],  r[10]);
    yaw   = Math.atan2(-r[8], sy);
    roll  = Math.atan2(r[4],  r[0]);
  } else {
    pitch = Math.atan2(-r[6], r[5]);
    yaw   = Math.atan2(-r[8], sy);
    roll  = 0;
  }

  return {
    pitch: pitch * (180 / Math.PI),
    yaw:   yaw   * (180 / Math.PI),
    roll:  roll  * (180 / Math.PI),
  };
}

export function analyzeHeadPose(
  transformMatrix: number[],
  previousPose: { pitch: number; yaw: number; roll: number } | null,
  deltaTimeSec: number
): HeadPoseResult {
  const pose = extractHeadPose(transformMatrix);

  const isLookingAway =
    Math.abs(pose.yaw) > 20 || Math.abs(pose.pitch) > 15;

  // Hareket hızı (derece/saniye)
  let headMovementRate = 0;
  if (previousPose && deltaTimeSec > 0) {
    const deltaPitch = Math.abs(pose.pitch - previousPose.pitch);
    const deltaYaw   = Math.abs(pose.yaw   - previousPose.yaw);
    const deltaRoll  = Math.abs(pose.roll  - previousPose.roll);
    const totalDelta = deltaPitch + deltaYaw + deltaRoll;
    headMovementRate = totalDelta / deltaTimeSec;
  }

  // Stabilite skoru (düşük hareket = yüksek stabilite)
  const headStability = Math.max(0, 1 - headMovementRate / 60);

  return { ...pose, isLookingAway, headStability, headMovementRate };
}
```

---

### 3.3 52 Blendshape Analizi

MediaPipe, ARKit ile uyumlu 52 blendshape üretir. Her biri 0.0–1.0 arası bir katsayıdır.

#### Tam Blendshape Listesi ve Mülakat Yorumları

```typescript
// lib/mediapipe/analyzers/blendshapeAnalyzer.ts

// Tüm 52 blendshape indeksi ve adı
export const BLENDSHAPE_NAMES = [
  "browDownLeft",        // 0  - Kaş çatma sol
  "browDownRight",       // 1  - Kaş çatma sağ
  "browInnerUp",         // 2  - İç kaş kaldırma (endişe/sürpriz)
  "browOuterUpLeft",     // 3  - Dış kaş kaldırma sol
  "browOuterUpRight",    // 4  - Dış kaş kaldırma sağ
  "cheekPuff",           // 5  - Yanak şişirme
  "cheekSquintLeft",     // 6  - Sol yanak kıstırma (gerçek gülümseme)
  "cheekSquintRight",    // 7  - Sağ yanak kıstırma
  "eyeBlinkLeft",        // 8  - Sol göz kırpma
  "eyeBlinkRight",       // 9  - Sağ göz kırpma
  "eyeLookDownLeft",     // 10 - Sol göz aşağı bakış
  "eyeLookDownRight",    // 11
  "eyeLookInLeft",       // 12 - Sol göz içe bakış
  "eyeLookInRight",      // 13
  "eyeLookOutLeft",      // 14 - Sol göz dışa bakış
  "eyeLookOutRight",     // 15
  "eyeLookUpLeft",       // 16 - Sol göz yukarı bakış
  "eyeLookUpRight",      // 17
  "eyeSquintLeft",       // 18 - Sol göz kıstırma (dikkat/şüphe)
  "eyeSquintRight",      // 19
  "eyeWideLeft",         // 20 - Sol göz açma (sürpriz/korku)
  "eyeWideRight",        // 21
  "jawForward",          // 22 - Çene öne
  "jawLeft",             // 23 - Çene sola
  "jawOpen",             // 24 - Ağız açma (tereddüt/hazırlıksızlık)
  "jawRight",            // 25 - Çene sağa
  "mouthClose",          // 26 - Ağız kapama
  "mouthDimpleLeft",     // 27 - Sol ağız çukuru
  "mouthDimpleRight",    // 28
  "mouthFrownLeft",      // 29 - Sol ağız köşesi aşağı (olumsuzluk)
  "mouthFrownRight",     // 30
  "mouthFunnel",         // 31 - Ağız huni (şüphe/tereddüt)
  "mouthLeft",           // 32 - Ağız sola kayma
  "mouthLowerDownLeft",  // 33
  "mouthLowerDownRight", // 34
  "mouthPressLeft",      // 35 - Sol dudak sıkıştırma (stres)
  "mouthPressRight",     // 36
  "mouthPucker",         // 37 - Dudak büzme
  "mouthRight",          // 38
  "mouthRollLower",      // 39 - Alt dudak içe alma
  "mouthRollUpper",      // 40 - Üst dudak içe alma
  "mouthShrugLower",     // 41
  "mouthShrugUpper",     // 42
  "mouthSmileLeft",      // 43 - Sol gülümseme
  "mouthSmileRight",     // 44 - Sağ gülümseme
  "mouthStretchLeft",    // 45 - Sol ağız germe (gerilim)
  "mouthStretchRight",   // 46
  "mouthUpperUpLeft",    // 47
  "mouthUpperUpRight",   // 48
  "noseSneerLeft",       // 49 - Sol burun kıvırma (rahatsızlık)
  "noseSneerRight",      // 50
  "tongueOut",           // 51 - Dil çıkarma
] as const;

export type BlendshapeName = (typeof BLENDSHAPE_NAMES)[number];

// Blendshape vektörü: indeks → skor
export type BlendshapeVector = Record<BlendshapeName, number>;

export function parseBlendshapes(
  classifications: any[]
): BlendshapeVector {
  const result = {} as BlendshapeVector;
  if (!classifications || classifications.length === 0) return result;

  classifications[0].categories.forEach((cat: any) => {
    result[cat.categoryName as BlendshapeName] = cat.score;
  });

  return result;
}

// ─── Türetilmiş Sinyaller ──────────────────────────────────────────

export interface DerivedSignals {
  // Göz sinyalleri
  blinkRate:          number; // Dakikadaki kırpma sayısı
  isBlinking:         boolean;
  eyeOpenness:        number; // 0-1 (eyeBlink'in tersi)
  eyeSquintIntensity: number; // Dikkat/şüphe göstergesi
  eyeWideIntensity:   number; // Sürpriz/korku göstergesi

  // Kaş sinyalleri
  browFurrow:         number; // Kaş çatma şiddeti (odaklanma/endişe)
  browRaise:          number; // Kaş kaldırma (sürpriz/açıklık)
  browAsymmetry:      number; // Sol-sağ fark (şüphe/ironi)

  // Ağız sinyalleri
  smileIntensity:     number; // Gülümseme gücü
  isDuchenne:         boolean; // Gerçek gülümseme (cheekSquint gerektirir)
  socialSmileRatio:   number; // Sosyal/sahte gülümseme oranı
  lipPress:           number; // Dudak sıkıştırma (stres/endişe)
  lipTension:         number; // Genel dudak gerilimi
  jawOpenness:        number; // Çene açıklığı (tereddüt/hazırlıksızlık)
  mouthFrown:         number; // Ağız köşesi aşağı (olumsuz duygu)

  // Burun sinyalleri
  noseSneer:          number; // Burun kıvırma (rahatsızlık/tiksinme)

  // Bileşik sinyaller
  arousalIndex:       number; // Genel uyarılma düzeyi (0-1)
  stressSignal:       number; // Stres kompoziti (0-1)
  discomfortSignal:   number; // Rahatsızlık kompoziti (0-1)
}

export function derivedSignals(bs: BlendshapeVector): DerivedSignals {
  // Göz
  const eyeOpenness = 1 - (bs.eyeBlinkLeft + bs.eyeBlinkRight) / 2;
  const eyeSquintIntensity = (bs.eyeSquintLeft + bs.eyeSquintRight) / 2;
  const eyeWideIntensity   = (bs.eyeWideLeft  + bs.eyeWideRight)  / 2;

  // Kaş
  const browFurrow    = (bs.browDownLeft + bs.browDownRight) / 2;
  const browRaise     = (bs.browInnerUp + bs.browOuterUpLeft + bs.browOuterUpRight) / 3;
  const browAsymmetry = Math.abs(bs.browDownLeft - bs.browDownRight);

  // Ağız
  const smileIntensity = (bs.mouthSmileLeft + bs.mouthSmileRight) / 2;
  const cheekSquint    = (bs.cheekSquintLeft + bs.cheekSquintRight) / 2;

  // Duchenne gülümseme: Gerçek gülümsemede yanak kasları da devreye girer
  // smileIntensity > 0.3 VE cheekSquint > 0.2 → Duchenne
  const isDuchenne = smileIntensity > 0.3 && cheekSquint > 0.2;

  // Sosyal gülümseme oranı: Dudak güler, yanak gülmez
  const socialSmileRatio = smileIntensity > 0.2
    ? Math.max(0, smileIntensity - cheekSquint)
    : 0;

  const lipPress    = (bs.mouthPressLeft + bs.mouthPressRight) / 2;
  const lipTension  = (bs.mouthStretchLeft + bs.mouthStretchRight) / 2;
  const jawOpenness = bs.jawOpen;
  const mouthFrown  = (bs.mouthFrownLeft + bs.mouthFrownRight) / 2;
  const noseSneer   = (bs.noseSneerLeft  + bs.noseSneerRight)  / 2;

  // ─── Bileşik İndeksler ───────────────────────────────────────────

  // Arousal (uyarılma): Yüzün genel aktivasyon düzeyi
  // Yüksek arousal = yüksek ifade yoğunluğu
  const arousalIndex = Math.min(1, (
    browFurrow * 0.15 +
    browRaise  * 0.15 +
    eyeWideIntensity * 0.15 +
    lipPress   * 0.15 +
    jawOpenness * 0.10 +
    noseSneer   * 0.10 +
    eyeSquintIntensity * 0.10 +
    smileIntensity * 0.10
  ));

  // Stres sinyali: Olumsuz yüksek uyarılma
  const stressSignal = Math.min(1, (
    lipPress     * 0.25 +
    browFurrow   * 0.20 +
    jawOpenness  * 0.15 +
    noseSneer    * 0.15 +
    eyeSquintIntensity * 0.10 +
    browAsymmetry * 0.15
  ));

  // Rahatsızlık sinyali
  const discomfortSignal = Math.min(1, (
    noseSneer    * 0.30 +
    mouthFrown   * 0.30 +
    lipPress     * 0.20 +
    browFurrow   * 0.20
  ));

  return {
    blinkRate: 0,         // TimeWindow'da hesaplanır
    isBlinking: false,    // EAR'dan gelir
    eyeOpenness,
    eyeSquintIntensity,
    eyeWideIntensity,
    browFurrow,
    browRaise,
    browAsymmetry,
    smileIntensity,
    isDuchenne,
    socialSmileRatio,
    lipPress,
    lipTension,
    jawOpenness,
    mouthFrown,
    noseSneer,
    arousalIndex,
    stressSignal,
    discomfortSignal,
  };
}
```

---

### 3.4 Bölgesel Yüz Analizi

```typescript
// lib/mediapipe/analyzers/regionalAnalyzer.ts

// Landmark bölge grupları
export const FACIAL_REGIONS = {
  // Alın bölgesi (10 landmark)
  FOREHEAD: [10, 108, 67, 69, 104, 103, 54, 21, 162, 127],

  // Sol kaş (8 landmark)
  LEFT_EYEBROW:  [70, 63, 105, 66, 107, 55, 65, 52],
  RIGHT_EYEBROW: [300, 293, 334, 296, 336, 285, 295, 282],

  // Sol göz bölgesi (16 landmark)
  LEFT_EYE:  [33, 7, 163, 144, 145, 153, 154, 155, 133, 246, 161, 160, 159, 158, 157, 173],
  RIGHT_EYE: [362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398],

  // Burun (9 landmark)
  NOSE:  [1, 2, 5, 4, 19, 94, 141, 370, 462],

  // Üst dudak (12 landmark)
  UPPER_LIP: [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 308],

  // Alt dudak
  LOWER_LIP: [146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 308, 324],

  // Yanak sol/sağ
  LEFT_CHEEK:  [116, 123, 147, 213, 192, 214, 210, 211, 32, 171],
  RIGHT_CHEEK: [345, 352, 376, 433, 416, 434, 430, 431, 262, 395],

  // Çene
  JAW: [172, 136, 150, 149, 176, 148, 152, 377, 400, 378, 379, 365, 397, 367, 435],
} as const;

// Bölge aktivasyon yoğunluğu hesapla (hareket büyüklüğü)
export function calculateRegionActivation(
  currentLandmarks: NormalizedLandmark[],
  previousLandmarks: NormalizedLandmark[],
  region: number[]
): number {
  if (!previousLandmarks) return 0;

  let totalMovement = 0;
  region.forEach((idx) => {
    totalMovement += euclidean(
      currentLandmarks[idx],
      previousLandmarks[idx]
    );
  });

  return totalMovement / region.length;
}

export interface RegionalActivationMap {
  forehead:    number;
  leftEyebrow: number;
  rightEyebrow: number;
  leftEye:     number;
  rightEye:    number;
  nose:        number;
  upperLip:    number;
  lowerLip:    number;
  leftCheek:   number;
  rightCheek:  number;
  jaw:         number;
  // Bölge asimetrisi (sol-sağ fark)
  eyebrowAsymmetry: number;
  cheekAsymmetry:   number;
  // Üst/alt yüz oranı
  upperFaceRatio: number;
}
```

---

## 4. Zaman Serisi ve Pencere Sistemi

Her analizin tek kareye değil, pencerelenmiş zaman serisine dayanması gerekir.

```typescript
// lib/mediapipe/timeWindow.ts

export interface FrameData {
  timestamp:    number;
  questionId:   string;
  ear:          number;
  gazeScore:    number;
  headPose:     { pitch: number; yaw: number; roll: number };
  blendshapes:  BlendshapeVector;
  derived:      DerivedSignals;
  isBlinking:   boolean;
}

export interface WindowAnalysis {
  // Temel istatistikler
  duration:       number; // ms
  frameCount:     number;

  // Göz
  avgEAR:         number;
  blinkCount:     number;
  blinkRate:      number; // dakika başına
  avgGazeScore:   number;
  gazeOffCount:   number; // Kameradan sapma sayısı
  gazeOnPercent:  number; // % kameraya bakma

  // Baş pozu
  avgPitch:       number;
  avgYaw:         number;
  avgRoll:        number;
  headStabilityScore: number;
  lookAwayCount:  number;
  lookAwayPercent: number;

  // Blendshape ortalamaları
  avgArousal:     number;
  avgStress:      number;
  avgDiscomfort:  number;
  peakArousal:    number;
  peakStress:     number;

  // Gülümseme
  avgSmile:       number;
  duchennePct:    number; // Gerçek gülümseme yüzdesi
  socialSmilePct: number;

  // Trend (son 5s vs ilk 5s)
  stressTrend:    "increasing" | "decreasing" | "stable";
  arousalTrend:   "increasing" | "decreasing" | "stable";

  // Kritik anlar
  peakMoments: Array<{
    timestamp: number;
    type: "stress_peak" | "look_away" | "blink_burst" | "arousal_spike";
    value: number;
  }>;
}

export class TimeWindowBuffer {
  private frames: FrameData[] = [];
  private readonly maxDurationMs: number;

  constructor(maxDurationMs = 30000) { // 30 saniyelik pencere
    this.maxDurationMs = maxDurationMs;
  }

  push(frame: FrameData): void {
    this.frames.push(frame);
    this.evictOld(frame.timestamp);
  }

  private evictOld(now: number): void {
    const cutoff = now - this.maxDurationMs;
    this.frames = this.frames.filter((f) => f.timestamp >= cutoff);
  }

  // Kayan pencere analizi
  analyze(windowMs = 30000): WindowAnalysis {
    const now = Date.now();
    const window = this.frames.filter(
      (f) => f.timestamp >= now - windowMs
    );

    if (window.length === 0) return this.emptyAnalysis();

    const duration = window[window.length - 1].timestamp - window[0].timestamp;

    // Göz kırpma sayısı (ardışık blink tespiti)
    const blinkCount = this.countBlinks(window);
    const blinkRate  = duration > 0 ? (blinkCount / duration) * 60000 : 0;

    // Ortalamalar
    const avgEAR       = avg(window.map((f) => f.ear));
    const avgGazeScore = avg(window.map((f) => f.gazeScore));
    const gazeOnCount  = window.filter((f) => f.gazeScore > 0.7).length;
    const gazeOnPercent = (gazeOnCount / window.length) * 100;

    const avgPitch = avg(window.map((f) => f.headPose.pitch));
    const avgYaw   = avg(window.map((f) => f.headPose.yaw));
    const avgRoll  = avg(window.map((f) => f.headPose.roll));

    const lookAwayFrames = window.filter(
      (f) => Math.abs(f.headPose.yaw) > 20 || Math.abs(f.headPose.pitch) > 15
    );
    const lookAwayPercent = (lookAwayFrames.length / window.length) * 100;

    const avgArousal   = avg(window.map((f) => f.derived.arousalIndex));
    const avgStress    = avg(window.map((f) => f.derived.stressSignal));
    const avgDiscomfort = avg(window.map((f) => f.derived.discomfortSignal));
    const peakArousal  = Math.max(...window.map((f) => f.derived.arousalIndex));
    const peakStress   = Math.max(...window.map((f) => f.derived.stressSignal));

    const avgSmile    = avg(window.map((f) => f.derived.smileIntensity));
    const duchennePct = (window.filter((f) => f.derived.isDuchenne).length / window.length) * 100;
    const socialSmilePct = (window.filter((f) => f.derived.socialSmileRatio > 0.15).length / window.length) * 100;

    // Trend hesabı (pencereyi ikiye böl)
    const mid = Math.floor(window.length / 2);
    const firstHalf  = window.slice(0, mid);
    const secondHalf = window.slice(mid);

    const stressTrend  = this.calcTrend(firstHalf, secondHalf, "stressSignal");
    const arousalTrend = this.calcTrend(firstHalf, secondHalf, "arousalIndex");

    // Kritik anlar
    const peakMoments = this.findPeakMoments(window, avgStress, avgArousal);

    return {
      duration, frameCount: window.length,
      avgEAR, blinkCount, blinkRate,
      avgGazeScore, gazeOffCount: window.length - gazeOnCount, gazeOnPercent,
      avgPitch, avgYaw, avgRoll,
      headStabilityScore: Math.max(0, 1 - Math.abs(avgYaw) / 45),
      lookAwayCount: lookAwayFrames.length, lookAwayPercent,
      avgArousal, avgStress, avgDiscomfort, peakArousal, peakStress,
      avgSmile, duchennePct, socialSmilePct,
      stressTrend, arousalTrend,
      peakMoments,
    };
  }

  // Soru segmenti analizi (soru başından sonuna kadar)
  analyzeSegment(startTime: number, endTime: number): WindowAnalysis {
    const segment = this.frames.filter(
      (f) => f.timestamp >= startTime && f.timestamp <= endTime
    );
    // analyze() ile aynı mantık, segment üzerinden
    return this.analyzeFrames(segment);
  }

  private countBlinks(frames: FrameData[]): number {
    let count = 0;
    let wasBlinking = false;
    frames.forEach((f) => {
      if (f.isBlinking && !wasBlinking) count++;
      wasBlinking = f.isBlinking;
    });
    return count;
  }

  private calcTrend(
    first: FrameData[],
    second: FrameData[],
    key: keyof DerivedSignals
  ): "increasing" | "decreasing" | "stable" {
    if (first.length === 0 || second.length === 0) return "stable";
    const a1 = avg(first.map((f)  => f.derived[key] as number));
    const a2 = avg(second.map((f) => f.derived[key] as number));
    const diff = a2 - a1;
    if (diff > 0.05)  return "increasing";
    if (diff < -0.05) return "decreasing";
    return "stable";
  }

  private findPeakMoments(
    frames: FrameData[],
    avgStress: number,
    avgArousal: number
  ) {
    const peaks: WindowAnalysis["peakMoments"] = [];
    frames.forEach((f) => {
      if (f.derived.stressSignal > avgStress + 0.2) {
        peaks.push({ timestamp: f.timestamp, type: "stress_peak", value: f.derived.stressSignal });
      }
      if (f.derived.arousalIndex > avgArousal + 0.25) {
        peaks.push({ timestamp: f.timestamp, type: "arousal_spike", value: f.derived.arousalIndex });
      }
      if (!f.gazeScore && f.gazeScore < 0.3) {
        peaks.push({ timestamp: f.timestamp, type: "look_away", value: f.gazeScore });
      }
    });
    return peaks;
  }

  private analyzeFrames(frames: FrameData[]): WindowAnalysis {
    // analyze() mantığının aynısı, parametre olarak frames alır
    // Kısa tutmak için burada sadece imza gösteriliyor
    return this.analyze();
  }

  private emptyAnalysis(): WindowAnalysis {
    return {
      duration: 0, frameCount: 0,
      avgEAR: 0, blinkCount: 0, blinkRate: 0,
      avgGazeScore: 0, gazeOffCount: 0, gazeOnPercent: 0,
      avgPitch: 0, avgYaw: 0, avgRoll: 0,
      headStabilityScore: 1, lookAwayCount: 0, lookAwayPercent: 0,
      avgArousal: 0, avgStress: 0, avgDiscomfort: 0, peakArousal: 0, peakStress: 0,
      avgSmile: 0, duchennePct: 0, socialSmilePct: 0,
      stressTrend: "stable", arousalTrend: "stable",
      peakMoments: [],
    };
  }
}

function avg(arr: number[]): number {
  if (arr.length === 0) return 0;
  return arr.reduce((a, b) => a + b, 0) / arr.length;
}
```

---

## 5. Sinyal Birleştirme ve Kompozit Skorlar

Birden fazla sinyali birleştirerek anlamlı davranışsal çıktılar üret.

```typescript
// lib/mediapipe/compositeScorer.ts

export interface CompositeScores {
  // Ana skorlar (0-100)
  overallPresence:    number; // Genel sunum etkinliği
  eyeContactScore:    number; // Göz teması kalitesi
  composure:          number; // Duygusal denge/sabitlik
  expressiveness:     number; // İfade zenginliği
  authenticity:       number; // Otantiklik

  // Davranışsal bayraklar
  flags: BehavioralFlag[];

  // Soru başlangıcı analizi (ilk 5s)
  openingReaction: "confident" | "hesitant" | "surprised" | "neutral";

  // Genel değerlendirme
  summary: string;
}

export interface BehavioralFlag {
  type:        string;
  severity:    "low" | "medium" | "high";
  timestamp?:  number;
  description: string;
}

export function calculateCompositeScores(
  windowAnalysis: WindowAnalysis,
  questionSegments: WindowAnalysis[]
): CompositeScores {
  const flags: BehavioralFlag[] = [];

  // ─── Göz Teması Skoru ─────────────────────────────────────
  const eyeContactScore = windowAnalysis.gazeOnPercent;

  if (eyeContactScore < 40) {
    flags.push({
      type: "low_eye_contact",
      severity: "high",
      description: `Göz teması %${eyeContactScore.toFixed(0)} — önerilen minimum %60`,
    });
  }

  // ─── Composure (Duygusal Denge) ────────────────────────────
  // Düşük stres + yüksek stabilite + düşük değişkenlik
  const stressStability = 1 - windowAnalysis.avgStress;
  const headStability   = windowAnalysis.headStabilityScore;
  const composure = Math.round(
    (stressStability * 0.5 + headStability * 0.3 +
     (1 - windowAnalysis.lookAwayPercent / 100) * 0.2) * 100
  );

  if (windowAnalysis.avgStress > 0.6) {
    flags.push({
      type: "high_stress",
      severity: "high",
      description: "Yüksek stres sinyali tespit edildi — özellikle dudak sıkıştırma ve kaş çatma",
    });
  }

  if (windowAnalysis.stressTrend === "increasing") {
    flags.push({
      type: "stress_escalation",
      severity: "medium",
      description: "Mülakat ilerledikçe stres artış trendi gözlemlendi",
    });
  }

  // ─── Expressiveness (İfade Zenginliği) ──────────────────────
  // Çok düşük ifade de, aşırı yüksek de olumsuz
  const expressiveness = Math.round(
    clamp(windowAnalysis.avgArousal * 1.5, 0, 1) * 100
  );

  if (windowAnalysis.avgArousal < 0.1) {
    flags.push({
      type: "flat_affect",
      severity: "medium",
      description: "Çok düşük yüz ifadesi — monoton/donuk görünüm riski",
    });
  }

  // ─── Authenticity (Otantiklik) ────────────────────────────
  // Duchenne gülümseme oranı yüksekse otantik
  const smileAuthenticity = windowAnalysis.duchennePct / 100;
  const expressionConsistency = 1 - Math.abs(
    windowAnalysis.avgArousal - windowAnalysis.avgSmile * 0.5
  );
  const authenticity = Math.round(
    (smileAuthenticity * 0.5 + expressionConsistency * 0.5) * 100
  );

  if (windowAnalysis.socialSmilePct > 60 && windowAnalysis.duchennePct < 20) {
    flags.push({
      type: "forced_smile",
      severity: "low",
      description: "Gülümsemenin büyük bölümü sosyal/göstermelik görünüyor (yanak kasları devreye girmiyor)",
    });
  }

  // ─── Göz Kırpma Analizi ────────────────────────────────────
  if (windowAnalysis.blinkRate > 30) {
    flags.push({
      type: "high_blink_rate",
      severity: "medium",
      description: `Yüksek göz kırpma frekansı: ${windowAnalysis.blinkRate.toFixed(0)}/dak — yüksek bilişsel yük işareti`,
    });
  }
  if (windowAnalysis.blinkRate < 8 && windowAnalysis.blinkRate > 0) {
    flags.push({
      type: "low_blink_rate",
      severity: "low",
      description: "Çok düşük göz kırpma — donma/aşırı odaklanma",
    });
  }

  // ─── Baş Pozu ─────────────────────────────────────────────
  if (windowAnalysis.lookAwayPercent > 25) {
    flags.push({
      type: "frequent_gaze_aversion",
      severity: "medium",
      description: `Zamanın %${windowAnalysis.lookAwayPercent.toFixed(0)}'inde başka yöne bakış`,
    });
  }

  // ─── Opening Reaction (Soruyu duyunca ilk 3s) ─────────────
  const openingReaction = determineOpeningReaction(
    questionSegments.length > 0 ? questionSegments[0] : null
  );

  // ─── Overall Presence ─────────────────────────────────────
  const overallPresence = Math.round(
    (eyeContactScore * 0.30 +
     composure       * 0.25 +
     expressiveness  * 0.20 +
     authenticity    * 0.15 +
     headStability   * 100 * 0.10)
  );

  return {
    overallPresence,
    eyeContactScore: Math.round(eyeContactScore),
    composure,
    expressiveness,
    authenticity,
    flags,
    openingReaction,
    summary: generateSummary({ overallPresence, eyeContactScore, composure, flags }),
  };
}

function determineOpeningReaction(
  firstSegment: WindowAnalysis | null
): CompositeScores["openingReaction"] {
  if (!firstSegment) return "neutral";

  if (firstSegment.avgArousal > 0.5 && firstSegment.avgStress < 0.3)
    return "confident";
  if (firstSegment.avgStress > 0.5)
    return "hesitant";
  if (firstSegment.peakArousal > 0.7 && firstSegment.avgArousal < 0.4)
    return "surprised";
  return "neutral";
}

function generateSummary(scores: Partial<CompositeScores>): string {
  const { overallPresence = 0, eyeContactScore = 0, composure = 0, flags = [] } = scores;

  if (overallPresence >= 80)
    return "Güçlü sunum — göz teması, sabitlik ve ifade dengesi iyi";
  if (overallPresence >= 60)
    return "Orta düzey sunum — bazı alanlarda geliştirme potansiyeli var";
  return "Gelişim gerektiren alanlar belirlendi — pratik önerilere bakın";
}

function clamp(v: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, v));
}
```

---

## 6. Web Worker Mimarisi

MediaPipe analizini ana thread'den ayır — UI donmamasın.

```typescript
// workers/faceAnalysis.worker.ts
// Bu dosya next.config.ts'de worker olarak tanımlanmalı

import { FaceLandmarkerService } from "../lib/mediapipe/faceLandmarker";
import { calculateEAR, detectBlink, calculateMeanEAR } from "../lib/mediapipe/analyzers/eyeAnalyzer";
import { analyzeGaze } from "../lib/mediapipe/analyzers/gazeAnalyzer";
import { analyzeHeadPose } from "../lib/mediapipe/analyzers/headPoseAnalyzer";
import { parseBlendshapes, derivedSignals } from "../lib/mediapipe/analyzers/blendshapeAnalyzer";
import { TimeWindowBuffer } from "../lib/mediapipe/timeWindow";

let landmarkerService: FaceLandmarkerService | null = null;
const buffer = new TimeWindowBuffer(30000);
let previousPose: any = null;
let lastTimestamp = 0;

self.onmessage = async (event: MessageEvent) => {
  const { type, payload } = event.data;

  switch (type) {
    case "INIT": {
      landmarkerService = new FaceLandmarkerService();
      await landmarkerService.initialize();
      self.postMessage({ type: "READY" });
      break;
    }

    case "ANALYZE_FRAME": {
      if (!landmarkerService) break;

      const { imageBitmap, timestamp, questionId, videoWidth, videoHeight } = payload;

      // OffscreenCanvas üzerinde çalış (Worker'da video yoktur)
      const canvas = new OffscreenCanvas(videoWidth, videoHeight);
      const ctx = canvas.getContext("2d")!;
      ctx.drawImage(imageBitmap, 0, 0);

      const result = landmarkerService.detectForVideo(
        canvas as any,
        timestamp
      );

      if (!result || !result.faceLandmarks || result.faceLandmarks.length === 0) {
        self.postMessage({ type: "NO_FACE" });
        break;
      }

      const landmarks   = result.faceLandmarks[0];
      const blendshapes = result.faceBlendshapes;
      const matrix      = result.facialTransformationMatrixes?.[0]?.data;

      const deltaTime = (timestamp - lastTimestamp) / 1000;
      lastTimestamp = timestamp;

      // ─── Sinyal çıkarımı ─────────────────────────────────
      const ear         = calculateMeanEAR(landmarks);
      const isBlinking  = detectBlink(ear);
      const gazeResult  = analyzeGaze(landmarks);
      const headPose    = matrix
        ? analyzeHeadPose(Array.from(matrix), previousPose, deltaTime)
        : null;
      const bsVector    = parseBlendshapes(blendshapes);
      const derived     = derivedSignals(bsVector);

      previousPose = headPose ? { pitch: headPose.pitch, yaw: headPose.yaw, roll: headPose.roll } : null;

      // ─── Buffer'a kaydet ─────────────────────────────────
      const frameData = {
        timestamp,
        questionId: questionId ?? "unknown",
        ear,
        gazeScore: gazeResult.eyeContactScore,
        headPose:  headPose ? { pitch: headPose.pitch, yaw: headPose.yaw, roll: headPose.roll } : { pitch: 0, yaw: 0, roll: 0 },
        blendshapes: bsVector,
        derived: { ...derived, blinkRate: 0, isBlinking },
        isBlinking,
      };

      buffer.push(frameData);

      // Her 30 frame'de bir pencere analizi gönder (~1 saniye)
      const windowAnalysis = buffer.analyze(10000); // 10 saniyelik pencere

      self.postMessage({
        type:   "FRAME_RESULT",
        payload: {
          // Real-time göstergeler
          realtime: {
            ear,
            isBlinking,
            gazeScore:    gazeResult.eyeContactScore,
            deviationAngle: gazeResult.deviationAngle,
            headPitch:    headPose?.pitch ?? 0,
            headYaw:      headPose?.yaw   ?? 0,
            headRoll:     headPose?.roll  ?? 0,
            arousal:      derived.arousalIndex,
            stress:       derived.stressSignal,
            isDuchenne:   derived.isDuchenne,
            smileIntensity: derived.smileIntensity,
          },
          // 10 saniyelik pencere özeti
          windowAnalysis,
        },
      });

      imageBitmap.close();
      break;
    }

    case "GET_SEGMENT_ANALYSIS": {
      const { startTime, endTime } = payload;
      const segmentAnalysis = buffer.analyzeSegment(startTime, endTime);
      self.postMessage({ type: "SEGMENT_RESULT", payload: segmentAnalysis });
      break;
    }

    case "RESET":
      break;
  }
};
```

### Worker'ı React Hook olarak kullanma

```typescript
// hooks/useFaceAnalysis.ts

import { useEffect, useRef, useCallback, useState } from "react";
import type { WindowAnalysis } from "../lib/mediapipe/timeWindow";

export interface RealtimeIndicators {
  ear:           number;
  isBlinking:    boolean;
  gazeScore:     number;
  deviationAngle: number;
  headPitch:     number;
  headYaw:       number;
  headRoll:      number;
  arousal:       number;
  stress:        number;
  isDuchenne:    boolean;
  smileIntensity: number;
}

export function useFaceAnalysis(videoRef: React.RefObject<HTMLVideoElement>) {
  const workerRef        = useRef<Worker | null>(null);
  const animFrameRef     = useRef<number>(0);
  const isReadyRef       = useRef(false);

  const [realtime, setRealtime]       = useState<RealtimeIndicators | null>(null);
  const [windowData, setWindowData]   = useState<WindowAnalysis | null>(null);
  const [faceDetected, setFaceDetected] = useState(false);

  useEffect(() => {
    workerRef.current = new Worker(
      new URL("../workers/faceAnalysis.worker.ts", import.meta.url),
      { type: "module" }
    );

    workerRef.current.onmessage = (e) => {
      const { type, payload } = e.data;
      switch (type) {
        case "READY":
          isReadyRef.current = true;
          break;
        case "FRAME_RESULT":
          setFaceDetected(true);
          setRealtime(payload.realtime);
          setWindowData(payload.windowAnalysis);
          break;
        case "NO_FACE":
          setFaceDetected(false);
          break;
        case "SEGMENT_RESULT":
          // Event ile dışarı ver
          window.dispatchEvent(new CustomEvent("segmentAnalysis", { detail: payload }));
          break;
      }
    };

    workerRef.current.postMessage({ type: "INIT" });

    return () => {
      cancelAnimationFrame(animFrameRef.current);
      workerRef.current?.terminate();
    };
  }, []);

  const startAnalysis = useCallback(
    (questionId: string) => {
      const video = videoRef.current;
      if (!video || !isReadyRef.current || !workerRef.current) return;

      const loop = () => {
        if (video.readyState >= 2) {
          const bitmap = (video as any).transferToImageBitmap?.();
          if (bitmap) {
            workerRef.current!.postMessage(
              {
                type: "ANALYZE_FRAME",
                payload: {
                  imageBitmap: bitmap,
                  timestamp:   performance.now(),
                  questionId,
                  videoWidth:  video.videoWidth,
                  videoHeight: video.videoHeight,
                },
              },
              [bitmap]
            );
          }
        }
        animFrameRef.current = requestAnimationFrame(loop);
      };

      animFrameRef.current = requestAnimationFrame(loop);
    },
    [videoRef]
  );

  const stopAnalysis = useCallback(() => {
    cancelAnimationFrame(animFrameRef.current);
  }, []);

  const requestSegmentAnalysis = useCallback(
    (startTime: number, endTime: number) => {
      workerRef.current?.postMessage({
        type: "GET_SEGMENT_ANALYSIS",
        payload: { startTime, endTime },
      });
    },
    []
  );

  return {
    realtime,
    windowData,
    faceDetected,
    startAnalysis,
    stopAnalysis,
    requestSegmentAnalysis,
  };
}
```

---

## 7. Python Backend Analizi

Mülakat bittikten sonra tüm frame verisini Python'da batch olarak işle.

```python
# python-service/routers/face_analysis.py

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel
from typing import List, Optional
import numpy as np
from scipy import stats
from scipy.signal import find_peaks

router = APIRouter(prefix="/face-analysis")


class FrameData(BaseModel):
    timestamp:    float
    questionId:   str
    ear:          float
    gazeScore:    float
    headPitch:    float
    headYaw:      float
    headRoll:     float
    arousal:      float
    stress:       float
    smileIntensity: float
    isDuchenne:   bool
    lipPress:     float
    browFurrow:   float
    jawOpen:      float


class PostInterviewRequest(BaseModel):
    interviewId: str
    frames:      List[FrameData]
    questions:   List[dict]  # {id, startTime, endTime, text}


class PostInterviewAnalysis(BaseModel):
    interviewId: str
    overall:     dict
    byQuestion:  List[dict]
    timeline:    dict
    insights:    List[str]


@router.post("/post-interview", response_model=PostInterviewAnalysis)
async def analyze_post_interview(req: PostInterviewRequest):
    if not req.frames:
        raise HTTPException(status_code=400, detail="No frame data")

    frames = req.frames
    ts     = np.array([f.timestamp for f in frames])

    # ─── Genel Metrikler ───────────────────────────────────────
    ear_arr     = np.array([f.ear for f in frames])
    gaze_arr    = np.array([f.gazeScore for f in frames])
    stress_arr  = np.array([f.stress for f in frames])
    arousal_arr = np.array([f.arousal for f in frames])
    smile_arr   = np.array([f.smileIntensity for f in frames])
    pitch_arr   = np.array([f.headPitch for f in frames])
    yaw_arr     = np.array([f.headYaw for f in frames])

    # Göz kırpma tespiti (EAR < 0.20 = kırpma)
    blink_mask   = ear_arr < 0.20
    blink_events = _count_blink_events(blink_mask)
    duration_min = (ts[-1] - ts[0]) / 60000
    blink_rate   = blink_events / duration_min if duration_min > 0 else 0

    # Stres pikleri
    stress_peaks, _ = find_peaks(stress_arr, height=0.6, distance=30)

    # Göz teması yüzdesi
    eye_contact_pct = float(np.mean(gaze_arr > 0.7) * 100)

    # Baş hareketi değişkenliği
    head_movement_var = float(np.std(yaw_arr) + np.std(pitch_arr))

    overall = {
        "avgStress":       float(np.mean(stress_arr)),
        "peakStress":      float(np.max(stress_arr)),
        "avgArousal":      float(np.mean(arousal_arr)),
        "blinkRatePerMin": float(blink_rate),
        "eyeContactPct":   eye_contact_pct,
        "avgSmile":        float(np.mean(smile_arr)),
        "duchennePct":     float(np.mean([f.isDuchenne for f in frames]) * 100),
        "headMovementVar": head_movement_var,
        "stressPeakCount": len(stress_peaks),
        "stressTrend":     _calc_trend(stress_arr),
    }

    # ─── Soru Bazlı Analiz ────────────────────────────────────
    by_question = []
    for q in req.questions:
        q_frames = [
            f for f in frames
            if q["startTime"] <= f.timestamp <= q["endTime"]
        ]
        if not q_frames:
            continue

        q_stress  = np.array([f.stress for f in q_frames])
        q_arousal = np.array([f.arousal for f in q_frames])
        q_gaze    = np.array([f.gazeScore for f in q_frames])

        # İlk 5 saniye vs son 5 saniye karşılaştırması
        fps_approx = len(q_frames) / max(1, (q["endTime"] - q["startTime"]) / 1000)
        first_5s   = q_frames[:int(fps_approx * 5)]
        last_5s    = q_frames[-int(fps_approx * 5):]

        opening_stress = float(np.mean([f.stress for f in first_5s])) if first_5s else 0
        closing_stress = float(np.mean([f.stress for f in last_5s]))  if last_5s  else 0

        by_question.append({
            "questionId":     q["id"],
            "questionText":   q.get("text", ""),
            "avgStress":      float(np.mean(q_stress)),
            "peakStress":     float(np.max(q_stress)),
            "avgArousal":     float(np.mean(q_arousal)),
            "eyeContactPct":  float(np.mean(q_gaze > 0.7) * 100),
            "openingStress":  opening_stress,
            "closingStress":  closing_stress,
            "stressResolved": closing_stress < opening_stress - 0.1,
            "difficulty":     _rate_difficulty(float(np.mean(q_stress)), float(np.mean(q_arousal))),
        })

    # ─── Zaman Çizelgesi ──────────────────────────────────────
    # 10'ar saniyelik dilimler
    timeline = _build_timeline(frames, interval_ms=10000)

    # ─── İçgörüler ────────────────────────────────────────────
    insights = _generate_insights(overall, by_question)

    return PostInterviewAnalysis(
        interviewId=req.interviewId,
        overall=overall,
        byQuestion=by_question,
        timeline=timeline,
        insights=insights,
    )


def _count_blink_events(mask: np.ndarray) -> int:
    count = 0
    was_blinking = False
    for b in mask:
        if b and not was_blinking:
            count += 1
        was_blinking = bool(b)
    return count


def _calc_trend(arr: np.ndarray) -> str:
    if len(arr) < 10:
        return "stable"
    mid   = len(arr) // 2
    first = float(np.mean(arr[:mid]))
    last  = float(np.mean(arr[mid:]))
    diff  = last - first
    if diff > 0.05:  return "increasing"
    if diff < -0.05: return "decreasing"
    return "stable"


def _rate_difficulty(stress: float, arousal: float) -> str:
    score = stress * 0.6 + arousal * 0.4
    if score > 0.6: return "hard"
    if score > 0.35: return "medium"
    return "easy"


def _build_timeline(frames: List[FrameData], interval_ms: int) -> dict:
    if not frames:
        return {}

    start = frames[0].timestamp
    end   = frames[-1].timestamp
    buckets: dict = {}

    t = start
    while t <= end:
        bucket_frames = [
            f for f in frames
            if t <= f.timestamp < t + interval_ms
        ]
        if bucket_frames:
            buckets[int((t - start) / 1000)] = {
                "stress":  float(np.mean([f.stress  for f in bucket_frames])),
                "arousal": float(np.mean([f.arousal for f in bucket_frames])),
                "gaze":    float(np.mean([f.gazeScore for f in bucket_frames])),
                "smile":   float(np.mean([f.smileIntensity for f in bucket_frames])),
            }
        t += interval_ms

    return buckets


def _generate_insights(overall: dict, by_question: List[dict]) -> List[str]:
    insights = []

    if overall["eyeContactPct"] < 50:
        insights.append(
            f"Göz teması %{overall['eyeContactPct']:.0f} — "
            "kameraya daha fazla bakış mülakat etkinliğini artırır"
        )

    if overall["blinkRatePerMin"] > 28:
        insights.append(
            f"Göz kırpma {overall['blinkRatePerMin']:.0f}/dak — "
            "yüksek bilişsel yük veya gerginlik işareti olabilir"
        )

    if overall["duchennePct"] < 20 and overall["avgSmile"] > 0.3:
        insights.append(
            "Gülümsemeler çoğunlukla sosyal — "
            "daha içten gülümsemeler güven oluşturur"
        )

    # En zor soruları bul
    hard_questions = [q for q in by_question if q.get("difficulty") == "hard"]
    if hard_questions:
        q_texts = ", ".join([f'"{q["questionText"][:40]}..."' for q in hard_questions[:2]])
        insights.append(
            f"En yüksek stres gözlemlenen sorular: {q_texts} — bu konulara ekstra pratik önerilir"
        )

    if overall["stressTrend"] == "increasing":
        insights.append(
            "Mülakat ilerledikçe stres artış trendi — "
            "dayanıklılık egzersizleri faydalı olabilir"
        )
    elif overall["stressTrend"] == "decreasing":
        insights.append(
            "Mülakat ilerledikçe stres azaldı — "
            "ısınma soruları stratejisi işe yarıyor"
        )

    return insights
```

---

## 8. Veri Modelleri (TypeScript)

```typescript
// types/faceAnalysis.ts

export interface InterviewFaceAnalysisResult {
  interviewId:    string;
  analyzedAt:     string; // ISO timestamp
  totalFrames:    number;
  durationSec:    number;

  overall: {
    // Temel metrikler
    eyeContactPercent:  number; // 0-100
    avgBlinkRate:       number; // dakika/kırpma
    avgGazeDeviation:   number; // derece
    headStabilityScore: number; // 0-100

    // İfade metrikleri
    avgArousalLevel:    number; // 0-100
    avgStressSignal:    number; // 0-100
    peakStressSignal:   number; // 0-100
    duchesneSmilePct:   number; // 0-100
    socialSmilePct:     number; // 0-100

    // Trend
    stressTrend:  "increasing" | "decreasing" | "stable";
    energyTrend:  "increasing" | "decreasing" | "stable";

    // Kompozit skorlar
    compositeScores: {
      overallPresence:  number;
      eyeContactScore:  number;
      composure:        number;
      expressiveness:   number;
      authenticity:     number;
    };

    // Bayraklar
    behavioralFlags: Array<{
      type:        string;
      severity:    "low" | "medium" | "high";
      description: string;
    }>;
  };

  byQuestion: Array<{
    questionId:      string;
    questionText:    string;
    startTime:       number;
    endTime:         number;
    avgStress:       number;
    peakStress:      number;
    eyeContactPct:   number;
    openingStress:   number;
    closingStress:   number;
    stressResolved:  boolean;
    difficulty:      "easy" | "medium" | "hard";
    openingReaction: "confident" | "hesitant" | "surprised" | "neutral";
  }>;

  timeline: Record<number, { // saniye: değer
    stress:  number;
    arousal: number;
    gaze:    number;
    smile:   number;
  }>;

  insights: string[];
}
```

---

## 9. Rapor Üretimi

```typescript
// lib/mediapipe/reportGenerator.ts

export function generateFaceAnalysisReport(
  analysis: InterviewFaceAnalysisResult
): string {
  const { overall, byQuestion, insights } = analysis;
  const scores = overall.compositeScores;

  const gradeMap = (score: number) => {
    if (score >= 85) return "Mükemmel";
    if (score >= 70) return "İyi";
    if (score >= 55) return "Orta";
    if (score >= 40) return "Geliştirilmeli";
    return "Kritik";
  };

  // En zorlu soru
  const hardestQ = byQuestion.reduce(
    (prev, curr) => (curr.avgStress > prev.avgStress ? curr : prev),
    byQuestion[0]
  );

  const report = `
## 🎯 Yüz Analizi Raporu

### Genel Skorlar
| Metrik              | Skor | Değerlendirme         |
|---------------------|------|-----------------------|
| Genel Sunum         | ${scores.overallPresence}/100  | ${gradeMap(scores.overallPresence)} |
| Göz Teması          | ${scores.eyeContactScore}/100  | ${gradeMap(scores.eyeContactScore)} |
| Duygusal Denge      | ${scores.composure}/100        | ${gradeMap(scores.composure)} |
| İfade Zenginliği    | ${scores.expressiveness}/100   | ${gradeMap(scores.expressiveness)} |
| Otantiklik          | ${scores.authenticity}/100     | ${gradeMap(scores.authenticity)} |

### Davranışsal Metrikler
- **Göz Teması:** %${overall.eyeContactPercent.toFixed(0)} (Önerilen: %60+)
- **Göz Kırpma:** ${overall.avgBlinkRate.toFixed(0)}/dak (Normal: 15-20)
- **Gerçek Gülümseme:** %${overall.duchesneSmilePct.toFixed(0)}
- **Stres Trendi:** ${overall.stressTrend === "increasing" ? "⬆️ Artıyor" : overall.stressTrend === "decreasing" ? "⬇️ Azalıyor" : "➡️ Sabit"}

### Soru Bazlı Analiz
${byQuestion.map((q, i) => `
**Soru ${i + 1}:** ${q.questionText.substring(0, 60)}...
- Zorluk: ${q.difficulty === "hard" ? "🔴 Zor" : q.difficulty === "medium" ? "🟡 Orta" : "🟢 Kolay"}
- Göz Teması: %${q.eyeContactPct.toFixed(0)}
- Tepki: ${q.openingReaction}
- Stres Çözümlendi mi: ${q.stressResolved ? "✅ Evet" : "❌ Hayır"}
`).join("")}

### 💡 Kişiselleştirilmiş Öneriler
${insights.map((i) => `- ${i}`).join("\n")}

### ⚠️ Dikkat Edilmesi Gereken Alanlar
${overall.behavioralFlags
  .filter((f) => f.severity !== "low")
  .map((f) => `- **${f.severity === "high" ? "🔴" : "🟡"} ${f.type}:** ${f.description}`)
  .join("\n")}
  `.trim();

  return report;
}
```

---

## 10. Landmark Referans Tablosu

### Kritik Landmark İndeksleri

| Bölge           | İndeksler                                    | Kullanım                    |
|-----------------|----------------------------------------------|-----------------------------|
| Sol göz üst kapak | 159, 160, 161                              | EAR hesabı                  |
| Sol göz alt kapak | 145, 144, 163                              | EAR hesabı                  |
| Sol göz köşeleri | 33, 133                                     | EAR yatay mesafe            |
| Sağ göz üst kapak | 386, 385, 384                              | EAR hesabı                  |
| Sağ göz alt kapak | 374, 373, 390                              | EAR hesabı                  |
| Sağ göz köşeleri | 362, 263                                    | EAR yatay mesafe            |
| Sol iris        | 468, 469, 470, 471, 472                      | Gaze tracking               |
| Sağ iris        | 473, 474, 475, 476, 477                      | Gaze tracking               |
| Burun ucu       | 1, 4                                         | Head pose referans           |
| Çene ucu        | 152                                          | Head pose referans           |
| Sol ağız köşesi | 61                                           | LAR / ağız analizi          |
| Sağ ağız köşesi | 291                                          | LAR / ağız analizi          |
| Üst dudak merkez | 0, 13                                       | Ağız açıklığı               |
| Alt dudak merkez | 17, 14                                      | Ağız açıklığı               |
| Alın merkezi    | 10                                           | Yüz merkezi referans        |

### Blendshape Öncelik Tablosu (Mülakat için)

| Öncelik | Blendshape          | Sinyal                          | Eşik  |
|---------|---------------------|---------------------------------|-------|
| ⭐⭐⭐  | eyeBlinkLeft/Right  | Stres / bilişsel yük            | > 0.5 |
| ⭐⭐⭐  | mouthPressLeft/Right| Gerginlik / endişe              | > 0.3 |
| ⭐⭐⭐  | browInnerUp         | Sürpriz / kaygı                 | > 0.4 |
| ⭐⭐⭐  | cheekSquintL/R      | Gerçek gülümseme (Duchenne)     | > 0.2 |
| ⭐⭐    | jawOpen             | Tereddüt / hazırlıksızlık      | > 0.4 |
| ⭐⭐    | browDownLeft/Right  | Odaklanma / endişe              | > 0.5 |
| ⭐⭐    | noseSneerLeft/Right | Rahatsızlık                     | > 0.3 |
| ⭐⭐    | mouthFrownL/R       | Olumsuz duygu                   | > 0.3 |
| ⭐      | mouthSmileL/R       | Gülümseme (tek başına yetersiz) | > 0.3 |
| ⭐      | eyeWideLeft/Right   | Sürpriz / korku                 | > 0.4 |
| ⭐      | eyeSquintL/R        | Şüphe / dikkat                  | > 0.4 |

---

## Kurulum Özeti

```bash
# 1. MediaPipe Vision Tasks
npm install @mediapipe/tasks-vision

# 2. next.config.ts'e worker desteği ekle
# experimental: { workerThreads: true }

# 3. Python bağımlılıkları
pip install fastapi numpy scipy --break-system-packages

# 4. Model dosyası otomatik CDN'den yüklenir
# Offline için: public/models/ altına kopyala
```

---

*Bu döküman `MEDIAPIPE_FACE_ANALYSIS_ARCHITECTURE.md` olarak kaydedildi. Tüm kod TypeScript (Next.js 14 App Router) ve Python (FastAPI) uyumludur.*
