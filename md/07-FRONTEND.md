# 07 — Frontend Yapısı

## Klasör Yapısı

Proje `src/` yapısını kullanır. `app/` klasörü sadece `src/app/` altındadır — root'ta ayrıca `app/` klasörü bulunmaz.

```
src/
├── app/                            # Next.js 14 App Router
│   ├── layout.tsx                  # Root layout (font, providers, toaster)
│   ├── page.tsx                    # Landing page
│   ├── error.tsx                   # Global error boundary
│   ├── not-found.tsx               # 404 sayfası
│   ├── loading.tsx                 # Global loading (nadiren kullanılır)
│   │
│   ├── (auth)/                     # Auth sayfaları (header/footer yok)
│   │   ├── layout.tsx
│   │   ├── login/
│   │   │   └── page.tsx
│   │   ├── register/
│   │   │   └── page.tsx
│   │   └── forgot-password/
│   │       └── page.tsx
│   │
│   ├── (dashboard)/                # Ana uygulama (sidebar layout)
│   │   ├── layout.tsx              # Sidebar + header
│   │   ├── dashboard/
│   │   │   ├── page.tsx            # Dashboard ana sayfa
│   │   │   └── loading.tsx
│   │   ├── interview/
│   │   │   ├── new/
│   │   │   │   └── page.tsx        # Mülakat oluşturma formu
│   │   │   ├── [id]/
│   │   │   │   ├── page.tsx        # Mülakat akışı (sorular, kayıt)
│   │   │   │   ├── loading.tsx
│   │   │   │   └── error.tsx
│   │   │   └── history/
│   │   │       └── page.tsx        # Geçmiş mülakatlar
│   │   ├── report/
│   │   │   └── [id]/
│   │   │       ├── page.tsx        # Analiz raporu
│   │   │       └── loading.tsx
│   │   ├── profile/
│   │   │   └── page.tsx
│   │   └── subscription/
│   │       └── page.tsx
│   │
│   └── api/                        # API Route'ları (bkz. 03-API_ENDPOINTS.md)
│
├── components/
│   ├── ui/                         # shadcn/ui temel bileşenler
│   │   ├── button.tsx
│   │   ├── card.tsx
│   │   ├── dialog.tsx
│   │   ├── input.tsx
│   │   ├── badge.tsx
│   │   ├── progress.tsx
│   │   ├── toast.tsx
│   │   └── ...
│   │
│   ├── interview/                  # Mülakat bileşenleri
│   │   ├── InterviewSetup.tsx      # Mülakat oluşturma formu
│   │   ├── InterviewRoom.tsx       # Ana mülakat arayüzü
│   │   ├── QuestionDisplay.tsx     # Soru gösterimi + timer
│   │   ├── RecordingControls.tsx   # Başlat/Durdur/Sonraki kontrolleri
│   │   ├── CameraPreview.tsx       # Webcam önizleme (küçük)
│   │   ├── TabSwitchWarning.tsx    # Sekme değişim uyarısı
│   │   └── InterviewProgress.tsx   # İlerleme göstergesi
│   │
│   ├── report/                     # Rapor bileşenleri
│   │   ├── ReportHeader.tsx        # Genel skor ve özet
│   │   ├── ScoreBreakdown.tsx      # Detaylı skor grafikleri
│   │   ├── QuestionReview.tsx      # Soru bazlı gözden geçirme
│   │   ├── VideoPlayer.tsx         # Video clip oynatıcı
│   │   ├── TranscriptView.tsx      # Transkript görüntüleyici
│   │   ├── BodyLanguageReport.tsx  # Yüz/beden dili analizi
│   │   └── ImprovementPlan.tsx     # Gelişim önerileri
│   │
│   ├── dashboard/
│   │   ├── StatsCard.tsx
│   │   ├── RecentInterviews.tsx
│   │   ├── ProgressChart.tsx
│   │   └── AchievementBadges.tsx
│   │
│   ├── layout/
│   │   ├── Sidebar.tsx
│   │   ├── Header.tsx
│   │   └── MobileNav.tsx
│   │
│   └── shared/
│       ├── LoadingSpinner.tsx
│       ├── ErrorState.tsx
│       ├── EmptyState.tsx
│       └── SubscriptionGate.tsx    # Feature gate bileşeni
│
├── hooks/
│   ├── use-dual-stream-recorder.ts # Video kayıt hook'u (bkz. 05-VIDEO_RECORDING.md)
│   ├── use-face-analysis.ts        # MediaPipe yüz analizi
│   ├── use-interview.ts            # Mülakat state yönetimi
│   ├── use-subscription.ts         # Abonelik durumu
│   └── use-debounce.ts
│
├── store/                          # Zustand store'ları
│   ├── auth-store.ts
│   ├── interview-store.ts
│   └── ui-store.ts
│
├── lib/
│   ├── prisma.ts                   # Prisma singleton
│   ├── ai/
│   │   ├── scribe.ts               # ElevenLabs STT
│   │   ├── question-generator.ts   # GPT-4 soru üretimi
│   │   ├── answer-evaluator.ts     # Cevap değerlendirme
│   │   ├── report-generator.ts     # Rapor oluşturma
│   │   └── keyterms.ts             # Keyterm prompting
│   ├── video/
│   │   ├── dual-stream-recorder.ts # Ana kayıt sınıfı
│   │   └── upload.ts               # S3 upload
│   ├── api/
│   │   ├── auth-guard.ts           # withAuth wrapper
│   │   └── rate-limit.ts           # Rate limiting
│   └── utils.ts                    # cn(), formatDate(), vs.
│
├── types/
│   ├── next-auth.d.ts
│   ├── interview.ts
│   └── analysis.ts
│
└── styles/
    └── globals.css
```

## Zustand Store'ları

```typescript
// src/store/interview-store.ts

import { create } from 'zustand';

interface InterviewQuestion {
  id: string;
  questionOrder: number;
  questionText: string;
  questionType: string;
  difficulty: string;
}

interface InterviewStore {
  // State
  interviewId: string | null;
  currentQuestion: InterviewQuestion | null;
  currentQuestionIndex: number;
  totalQuestions: number;
  status: 'idle' | 'recording' | 'submitting' | 'completed';
  tabSwitchCount: number;
  elapsedSeconds: number;

  // Actions
  setInterview: (id: string, totalQuestions: number) => void;
  setCurrentQuestion: (question: InterviewQuestion, index: number) => void;
  setStatus: (status: InterviewStore['status']) => void;
  incrementTabSwitch: () => void;
  incrementElapsed: () => void;
  reset: () => void;
}

export const useInterviewStore = create<InterviewStore>((set) => ({
  interviewId: null,
  currentQuestion: null,
  currentQuestionIndex: 0,
  totalQuestions: 10,
  status: 'idle',
  tabSwitchCount: 0,
  elapsedSeconds: 0,

  setInterview: (id, totalQuestions) =>
    set({ interviewId: id, totalQuestions }),
  setCurrentQuestion: (question, index) =>
    set({ currentQuestion: question, currentQuestionIndex: index }),
  setStatus: (status) => set({ status }),
  incrementTabSwitch: () =>
    set((s) => ({ tabSwitchCount: s.tabSwitchCount + 1 })),
  incrementElapsed: () =>
    set((s) => ({ elapsedSeconds: s.elapsedSeconds + 1 })),
  reset: () =>
    set({
      interviewId: null,
      currentQuestion: null,
      currentQuestionIndex: 0,
      status: 'idle',
      tabSwitchCount: 0,
      elapsedSeconds: 0,
    }),
}));
```

## InterviewRoom Bileşeni (Ana Mülakat Arayüzü)

```typescript
// src/components/interview/InterviewRoom.tsx

'use client';

import { useEffect, useRef } from 'react';
import { useDualStreamRecorder } from '@/hooks/use-dual-stream-recorder';
import { useInterviewStore } from '@/store/interview-store';
import { QuestionDisplay } from './QuestionDisplay';
import { RecordingControls } from './RecordingControls';
import { CameraPreview } from './CameraPreview';
import { TabSwitchWarning } from './TabSwitchWarning';
import { InterviewProgress } from './InterviewProgress';

interface InterviewRoomProps {
  interviewId: string;
  firstQuestion: { id: string; questionText: string; questionOrder: number };
  totalQuestions: number;
}

export function InterviewRoom({
  interviewId,
  firstQuestion,
  totalQuestions,
}: InterviewRoomProps) {
  const store = useInterviewStore();
  const {
    state: recorderState,
    tabSwitchCount,
    initialize,
    startClip,
    stopClipAndSubmit,
    stopAll,
    getCameraStream,
  } = useDualStreamRecorder(interviewId);

  // Mülakat başlat
  useEffect(() => {
    store.setInterview(interviewId, totalQuestions);
    store.setCurrentQuestion(firstQuestion, 0);

    initialize().then(() => {
      startClip(firstQuestion.id);
      store.setStatus('recording');
    });

    return () => {
      stopAll();
      store.reset();
    };
  }, [interviewId]);

  const handleNext = async () => {
    if (store.status !== 'recording' || !store.currentQuestion) return;

    store.setStatus('submitting');

    // Clip'i durdur ve arka planda yükle
    await stopClipAndSubmit(store.currentQuestion.id);

    // Sonraki soruyu API'den al
    const res = await fetch(`/api/interview/${interviewId}/generate-question`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ questionIndex: store.currentQuestionIndex + 1 }),
    });
    const { question } = await res.json();

    store.setCurrentQuestion(question, store.currentQuestionIndex + 1);
    startClip(question.id);
    store.setStatus('recording');
  };

  const handleFinish = async () => {
    if (!store.currentQuestion) return;
    store.setStatus('submitting');

    await stopClipAndSubmit(store.currentQuestion.id);
    await stopAll();

    await fetch(`/api/interview/${interviewId}/complete`, { method: 'POST' });
    window.location.href = `/report/${interviewId}`;
  };

  return (
    <div className="min-h-screen bg-background flex flex-col">
      <InterviewProgress
        current={store.currentQuestionIndex + 1}
        total={totalQuestions}
        elapsed={store.elapsedSeconds}
      />

      {tabSwitchCount > 0 && (
        <TabSwitchWarning count={tabSwitchCount} />
      )}

      <main className="flex-1 flex items-center justify-center p-6">
        <div className="w-full max-w-3xl space-y-6">
          {store.currentQuestion && (
            <QuestionDisplay
              question={store.currentQuestion.questionText}
              questionNumber={store.currentQuestionIndex + 1}
              isRecording={store.status === 'recording'}
            />
          )}

          <RecordingControls
            status={store.status}
            isLastQuestion={
              store.currentQuestionIndex >= totalQuestions - 1
            }
            onNext={handleNext}
            onFinish={handleFinish}
          />
        </div>
      </main>

      <CameraPreview stream={getCameraStream()} />
    </div>
  );
}
```

## Error Boundaries

```typescript
// src/app/error.tsx — Global error boundary
'use client';
export default function GlobalError({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  return (
    <div className="flex min-h-screen items-center justify-center">
      <div className="text-center space-y-4">
        <h2 className="text-xl font-medium">Bir hata oluştu</h2>
        <p className="text-muted-foreground">{error.message}</p>
        <button onClick={reset} className="btn-primary">
          Tekrar Dene
        </button>
      </div>
    </div>
  );
}

// src/app/not-found.tsx
export default function NotFound() {
  return (
    <div className="flex min-h-screen items-center justify-center">
      <div className="text-center space-y-4">
        <h1 className="text-4xl font-medium">404</h1>
        <p className="text-muted-foreground">Sayfa bulunamadı</p>
        <a href="/dashboard" className="btn-primary">
          Dashboard&apos;a Dön
        </a>
      </div>
    </div>
  );
}
```

## SubscriptionGate Bileşeni

Premium özellikler için gate bileşeni:

```typescript
// src/components/shared/SubscriptionGate.tsx

import { useSession } from 'next-auth/react';

const TIER_ORDER = { FREE: 0, BASIC: 1, PRO: 2, PREMIUM: 3 };

interface SubscriptionGateProps {
  requiredTier: 'BASIC' | 'PRO' | 'PREMIUM';
  children: React.ReactNode;
  fallback?: React.ReactNode;
}

export function SubscriptionGate({
  requiredTier,
  children,
  fallback,
}: SubscriptionGateProps) {
  const { data: session } = useSession();
  const userTier = (session?.user?.subscriptionTier ?? 'FREE') as keyof typeof TIER_ORDER;

  if (TIER_ORDER[userTier] >= TIER_ORDER[requiredTier]) {
    return <>{children}</>;
  }

  return (
    <>
      {fallback ?? (
        <div className="rounded-lg border border-dashed p-6 text-center">
          <p className="text-muted-foreground">
            Bu özellik için{' '}
            <span className="font-medium text-foreground">{requiredTier}</span>{' '}
            planı gereklidir.
          </p>
          <a href="/subscription" className="btn-primary mt-3 inline-block">
            Planı Yükselt
          </a>
        </div>
      )}
    </>
  );
}
```
