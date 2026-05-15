# 10 — Monetizasyon

## Paket Yapısı

| Özellik | Free | Basic | Pro | Premium |
|---------|------|-------|-----|---------|
| Aylık mülakat hakkı | 3 | 10 | 30 | Sınırsız |
| Soru başına AI kalitesi | Standart | Gelişmiş | Gelişmiş | En İyi |
| Transkripsiyon (Scribe v2) | Hayır | Evet | Evet | Evet |
| Video kayıt (Ekran + PiP) | Hayır | Evet | Evet | Evet |
| Video analizi (yüz/beden) | Hayır | Hayır | Evet | Evet |
| Ses analizi (pitch, tempo) | Hayır | Hayır | Evet | Evet |
| Rapor detayı | Basit | Detaylı | Tam | Tam + PDF |
| PDF export | Hayır | Hayır | Evet | Evet |
| Şirket analizi | Yok | 5/ay | 20/ay | Sınırsız |
| Mülakat süresi limiti | 10 dk | 30 dk | 60 dk | Sınırsız |
| Geçmiş saklama | 7 gün | 30 gün | 90 gün | 1 yıl |
| Sektör karşılaştırması | Hayır | Hayır | Evet | Evet |
| Öncelikli destek | Hayır | Hayır | Hayır | Evet |
| **Fiyat (aylık)** | **Ücretsiz** | **₺149** | **₺299** | **₺499** |
| **Fiyat (yıllık)** | — | **₺1.199** | **₺2.399** | **₺3.999** |

## Subscription Tier Kontrolleri

```typescript
// src/lib/subscription/limits.ts

export const TIER_LIMITS = {
  FREE: {
    monthlyInterviews: 3,
    maxDurationMinutes: 10,
    historyDays: 7,
    companyAnalysis: 0,
    features: [] as string[],
  },
  BASIC: {
    monthlyInterviews: 10,
    maxDurationMinutes: 30,
    historyDays: 30,
    companyAnalysis: 5,
    features: ['transcription', 'video_recording', 'detailed_report'],
  },
  PRO: {
    monthlyInterviews: 30,
    maxDurationMinutes: 60,
    historyDays: 90,
    companyAnalysis: 20,
    features: [
      'transcription', 'video_recording', 'detailed_report',
      'video_analysis', 'audio_analysis', 'pdf_export', 'industry_comparison'
    ],
  },
  PREMIUM: {
    monthlyInterviews: Infinity,
    maxDurationMinutes: Infinity,
    historyDays: 365,
    companyAnalysis: Infinity,
    features: [
      'transcription', 'video_recording', 'detailed_report',
      'video_analysis', 'audio_analysis', 'pdf_export', 'industry_comparison',
      'priority_support'
    ],
  },
} as const;

export type SubscriptionTier = keyof typeof TIER_LIMITS;

export function hasFeature(
  tier: SubscriptionTier,
  feature: string
): boolean {
  return TIER_LIMITS[tier].features.includes(feature);
}

export function canStartInterview(
  tier: SubscriptionTier,
  usedThisMonth: number
): boolean {
  const limit = TIER_LIMITS[tier].monthlyInterviews;
  return usedThisMonth < limit;
}
```

## Stripe Entegrasyonu

```typescript
// src/app/api/payment/create-checkout/route.ts

import Stripe from 'stripe';
import { withAuth } from '@/lib/api/auth-guard';
import { NextResponse } from 'next/server';
import { z } from 'zod';

const stripe = new Stripe(process.env.STRIPE_SECRET_KEY!);

const PRICE_IDS = {
  BASIC_MONTHLY:   'price_basic_monthly_xxx',
  BASIC_YEARLY:    'price_basic_yearly_xxx',
  PRO_MONTHLY:     'price_pro_monthly_xxx',
  PRO_YEARLY:      'price_pro_yearly_xxx',
  PREMIUM_MONTHLY: 'price_premium_monthly_xxx',
  PREMIUM_YEARLY:  'price_premium_yearly_xxx',
} as const;

const CheckoutSchema = z.object({
  tier: z.enum(['BASIC', 'PRO', 'PREMIUM']),
  interval: z.enum(['monthly', 'yearly']),
});

export const POST = withAuth(async (req, { user }) => {
  const body = await req.json();
  const parsed = CheckoutSchema.safeParse(body);
  if (!parsed.success) {
    return NextResponse.json({ error: 'Geçersiz paket' }, { status: 400 });
  }

  const { tier, interval } = parsed.data;
  const priceKey = `${tier}_${interval.toUpperCase()}` as keyof typeof PRICE_IDS;
  const priceId = PRICE_IDS[priceKey];

  const session = await stripe.checkout.sessions.create({
    mode: 'subscription',
    payment_method_types: ['card'],
    line_items: [{ price: priceId, quantity: 1 }],
    success_url: `${process.env.FRONTEND_URL}/subscription?success=true`,
    cancel_url: `${process.env.FRONTEND_URL}/subscription?canceled=true`,
    metadata: {
      userId: user.id,
      tier,
    },
    subscription_data: {
      metadata: { userId: user.id, tier },
    },
  });

  return NextResponse.json({ url: session.url });
});
```

## Stripe Webhook Handler

```typescript
// src/app/api/payment/webhook/route.ts

import Stripe from 'stripe';
import { NextRequest, NextResponse } from 'next/server';
import { prisma } from '@/lib/prisma';

const stripe = new Stripe(process.env.STRIPE_SECRET_KEY!);

export async function POST(req: NextRequest) {
  const body = await req.text();
  const sig = req.headers.get('stripe-signature')!;

  let event: Stripe.Event;
  try {
    event = stripe.webhooks.constructEvent(
      body,
      sig,
      process.env.STRIPE_WEBHOOK_SECRET!
    );
  } catch {
    return NextResponse.json({ error: 'Invalid signature' }, { status: 400 });
  }

  switch (event.type) {
    case 'checkout.session.completed': {
      const session = event.data.object as Stripe.CheckoutSession;
      const { userId, tier } = session.metadata!;

      await prisma.user.update({
        where: { id: userId },
        data: {
          subscriptionTier: tier as any,
          subscriptionEndsAt: new Date(Date.now() + 30 * 24 * 60 * 60 * 1000),
        },
      });

      await prisma.payment.create({
        data: {
          userId,
          amount: (session.amount_total ?? 0) / 100,
          currency: session.currency?.toUpperCase() ?? 'TRY',
          status: 'COMPLETED',
          paymentProvider: 'stripe',
          paymentId: session.id,
          packageType: tier,
        },
      });
      break;
    }

    case 'customer.subscription.deleted': {
      const subscription = event.data.object as Stripe.Subscription;
      const userId = subscription.metadata.userId;

      await prisma.user.update({
        where: { id: userId },
        data: {
          subscriptionTier: 'FREE',
          subscriptionEndsAt: null,
        },
      });
      break;
    }
  }

  return NextResponse.json({ received: true });
}
```

## Feature Gate — API Katmanı

```typescript
// src/lib/subscription/feature-gate.ts

import { prisma } from '@/lib/prisma';
import { TIER_LIMITS, hasFeature, canStartInterview } from './limits';
import { NextResponse } from 'next/server';

export async function checkInterviewLimit(userId: string, tier: string) {
  // Bu ay kaç mülakat yapılmış?
  const startOfMonth = new Date();
  startOfMonth.setDate(1);
  startOfMonth.setHours(0, 0, 0, 0);

  const count = await prisma.interview.count({
    where: {
      userId,
      createdAt: { gte: startOfMonth },
      status: { not: 'DRAFT' },
    },
  });

  if (!canStartInterview(tier as any, count)) {
    const limit = TIER_LIMITS[tier as keyof typeof TIER_LIMITS].monthlyInterviews;
    return NextResponse.json(
      {
        error: 'Aylık mülakat limitine ulaştınız',
        limit,
        used: count,
        upgradeUrl: '/subscription',
      },
      { status: 403 }
    );
  }

  return null;
}

export function checkFeatureAccess(tier: string, feature: string) {
  if (!hasFeature(tier as any, feature)) {
    return NextResponse.json(
      {
        error: `Bu özellik için plan yükseltmesi gerekiyor`,
        feature,
        upgradeUrl: '/subscription',
      },
      { status: 403 }
    );
  }
  return null;
}
```

## Kredi Sistemi

Free kullanıcılar ek kredi satın alabilir (abonelik olmadan tek seferlik mülakat):

| Paket | Kredi | Fiyat |
|-------|-------|-------|
| 1 Mülakat | 1 kredi | ₺29 |
| 5 Mülakat | 5 kredi | ₺119 |
| 10 Mülakat | 10 kredi | ₺199 |

Kredi kullanımı:
- Mülakat başlatılırken `credits > 0` kontrolü
- Başlatıldığında `credits -= 1`
- Başarısız mülakatlarda (teknik hata) kredi iade edilir

## Stripe Test Kartları

Geliştirme sırasında:

| Kart | Sonuç |
|------|-------|
| `4242 4242 4242 4242` | Başarılı ödeme |
| `4000 0000 0000 9995` | Yetersiz bakiye |
| `4000 0025 0000 3155` | 3D Secure gerekli |

Tarih: gelecekte herhangi bir tarih. CVC: herhangi 3 hane.
