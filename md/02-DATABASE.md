# 02 — Veritabanı Şeması

## Genel Yapı

Tüm kalıcı veri PostgreSQL'de saklanır (Prisma ORM). Redis yalnızca cache ve rate limiting için kullanılır. MongoDB orijinal tasarımdan çıkarılmıştır.

## Prisma Schema

```prisma
// prisma/schema.prisma

generator client {
  provider = "prisma-client-js"
}

datasource db {
  provider = "postgresql"
  url      = env("DATABASE_URL")
}

// =============================================
// KULLANICI YÖNETİMİ
// =============================================

model User {
  id                String    @id @default(cuid())
  email             String    @unique
  emailVerified     DateTime?
  password          String?
  name              String?
  image             String?
  phone             String?

  // Abonelik
  subscriptionTier  SubscriptionTier @default(FREE)
  credits           Int              @default(5)
  subscriptionEndsAt DateTime?

  // Profil
  currentPosition   String?
  targetPosition    String?
  industry          String?
  experienceYears   Int?
  cvUrl             String?
  linkedinUrl       String?

  // Tercihler
  preferredLanguage String  @default("tr")
  emailNotifications Boolean @default(true)

  // İlişkiler
  interviews        Interview[]
  payments          Payment[]
  achievements      UserAchievement[]
  sessions          Session[]
  accounts          Account[]

  createdAt         DateTime @default(now())
  updatedAt         DateTime @updatedAt

  @@index([email])
  @@index([subscriptionTier])
}

enum SubscriptionTier {
  FREE
  BASIC
  PRO
  PREMIUM
}

model Account {
  id                String  @id @default(cuid())
  userId            String
  type              String
  provider          String
  providerAccountId String
  refresh_token     String? @db.Text
  access_token      String? @db.Text
  expires_at        Int?
  token_type        String?
  scope             String?
  id_token          String? @db.Text
  session_state     String?

  user User @relation(fields: [userId], references: [id], onDelete: Cascade)

  @@unique([provider, providerAccountId])
  @@index([userId])
}

model Session {
  id           String   @id @default(cuid())
  sessionToken String   @unique
  userId       String
  expires      DateTime
  user         User     @relation(fields: [userId], references: [id], onDelete: Cascade)

  @@index([userId])
}

model VerificationToken {
  identifier String
  token      String   @unique
  expires    DateTime

  @@unique([identifier, token])
}

// =============================================
// MÜLAKAT SİSTEMİ
// =============================================

model Interview {
  id              String          @id @default(cuid())
  userId          String
  user            User            @relation(fields: [userId], references: [id], onDelete: Cascade)

  // Mülakat yapılandırması
  title           String
  companyName     String
  position        String
  industry        String
  department      String?
  experienceLevel String          // junior, mid, senior, lead
  interviewType   InterviewType   @default(BEHAVIORAL)
  language        String          @default("tr")

  // Şirket verileri (JSON olarak saklanır)
  companyData     Json?           // {description, culture, values, recentNews}
  jobDescription  String?         @db.Text
  jobRequirements String[]

  // Durum
  status          InterviewStatus @default(DRAFT)
  currentQuestion Int             @default(0)
  totalQuestions  Int             @default(10)

  // Zamanlama
  startedAt       DateTime?
  completedAt     DateTime?
  duration        Int?            // saniye

  // Genel skorlar (analiz sonrası doldurulur)
  overallScore    Float?
  confidence      Float?
  communication   Float?
  technicalScore  Float?

  // Video kayıt URL'leri
  fullRecordingUrl  String?       // Sürekli kayıt (S3)

  // İlişkiler
  questions       InterviewQuestion[]
  responses       InterviewResponse[]
  analysis        InterviewAnalysis?

  createdAt       DateTime        @default(now())
  updatedAt       DateTime        @updatedAt

  @@index([userId])
  @@index([status])
  @@index([createdAt])
}

enum InterviewType {
  BEHAVIORAL      // Davranışsal
  TECHNICAL       // Teknik
  CASE_STUDY      // Vaka analizi
  COMPETENCY      // Yetkinlik
  STRESS          // Stres mülakatı
  MIXED           // Karışık
}

enum InterviewStatus {
  DRAFT           // Hazırlık
  IN_PROGRESS     // Devam ediyor
  PAUSED          // Duraklatıldı
  COMPLETED       // Tamamlandı
  ANALYZING       // Analiz ediliyor
  ABANDONED       // Yarım bırakıldı
}

model InterviewQuestion {
  id              String   @id @default(cuid())
  interviewId     String
  interview       Interview @relation(fields: [interviewId], references: [id], onDelete: Cascade)

  questionOrder   Int
  questionText    String   @db.Text
  questionType    String   // behavioral, technical, situational
  difficulty      String   // easy, medium, hard
  category        String   // leadership, problem-solving, technical-skill

  // AI bağlamı
  aiContext       Json?
  expectedKeywords String[]

  // Zamanlama
  askedAt         DateTime?
  timeLimit       Int?     // saniye

  // İlişkiler
  response        InterviewResponse?

  createdAt       DateTime @default(now())

  @@index([interviewId])
  @@unique([interviewId, questionOrder])
}

model InterviewResponse {
  id              String   @id @default(cuid())
  questionId      String   @unique
  question        InterviewQuestion @relation(fields: [questionId], references: [id], onDelete: Cascade)
  interviewId     String
  interview       Interview @relation(fields: [interviewId], references: [id], onDelete: Cascade)

  // Kayıt dosyaları (S3 URL'leri)
  videoClipUrl    String?          // Soru bazlı video clip (ekran + webcam PiP)
  audioUrl        String?          // Sadece ses kanalı
  duration        Int              // saniye

  // Transkripsiyon (ElevenLabs Scribe v2 Batch)
  transcription   String?  @db.Text
  transcriptionStatus TranscriptionStatus @default(PENDING)
  scribeJobId     String?          // ElevenLabs async job ID

  // Analiz skorları
  sentimentScore  Float?
  clarityScore    Float?
  relevanceScore  Float?
  confidenceScore Float?

  // Video analizi (JSON)
  faceAnalysis    Json?    // {emotions, eyeContact, facialMovements}
  postureAnalysis Json?    // {posture, gestures, bodyLanguage}

  // Ses analizi (JSON)
  audioAnalysis   Json?    // {pitch, tempo, pauses, fillerWords, energy}

  // Zaman damgaları
  startedAt       DateTime
  submittedAt     DateTime

  createdAt       DateTime @default(now())

  @@index([interviewId])
}

enum TranscriptionStatus {
  PENDING         // Henüz gönderilmedi
  PROCESSING      // Scribe v2 işliyor
  COMPLETED       // Transkript hazır
  FAILED          // Hata oluştu
}

// =============================================
// ANALİZ & RAPORLAMA
// =============================================

model InterviewAnalysis {
  id              String   @id @default(cuid())
  interviewId     String   @unique
  interview       Interview @relation(fields: [interviewId], references: [id], onDelete: Cascade)

  // Genel metrikler
  overallScore    Float
  overallFeedback String   @db.Text

  // Detaylı skorlar
  communicationScore    Float
  technicalScore        Float
  confidenceScore       Float
  clarityScore          Float
  bodyLanguageScore     Float
  responseRelevance     Float

  // Davranışsal analiz
  eyeContactPercentage  Float
  smileFrequency        Float
  nervousGestures       Int
  postureQuality        Float

  // Konuşma analizi
  averageSpeechRate     Float   // kelime/dakika
  fillerWordCount       Int
  pauseFrequency        Float
  voiceConfidence       Float

  // Güçlü/zayıf yönler
  strengths         String[]
  weaknesses        String[]
  improvements      String[]

  // Soru bazlı geri bildirim (JSON)
  questionFeedback  Json

  // Sektör karşılaştırması
  industryPercentile Float?
  positionPercentile Float?

  // AI özeti
  aiSummary         String   @db.Text

  generatedAt       DateTime @default(now())

  @@index([interviewId])
}

// =============================================
// ŞİRKET VERİLERİ
// =============================================

model CompanyProfile {
  id              String   @id @default(cuid())
  companyName     String   @unique

  industry        String
  size            String?  // startup, small, medium, large, enterprise
  location        String?
  website         String?

  // Dış kaynak verileri
  linkedinUrl     String?
  scrapedData     Json?    // {linkedin, glassdoor, news}

  // Mülakat bilgileri
  commonQuestions String[]
  interviewStyle  String?
  cultureFit      String[]

  // Cache bilgisi
  lastScrapedAt   DateTime?

  createdAt       DateTime @default(now())
  updatedAt       DateTime @updatedAt

  @@index([companyName])
  @@index([industry])
}

// =============================================
// ÖDEME
// =============================================

model Payment {
  id              String   @id @default(cuid())
  userId          String
  user            User     @relation(fields: [userId], references: [id], onDelete: Cascade)

  amount          Float
  currency        String   @default("TRY")
  status          PaymentStatus

  paymentProvider String   // stripe, iyzico
  paymentId       String?  @unique

  packageType     String   // basic, pro, premium, credits
  creditsAdded    Int?
  subscriptionDays Int?

  metadata        Json?

  createdAt       DateTime @default(now())

  @@index([userId])
  @@index([status])
}

enum PaymentStatus {
  PENDING
  COMPLETED
  FAILED
  REFUNDED
}

// =============================================
// BAŞARIMLAR (Gamification)
// =============================================

model Achievement {
  id              String   @id @default(cuid())
  code            String   @unique
  name            String
  description     String
  icon            String
  category        String   // milestone, skill, consistency

  requiredCount   Int
  requiredScore   Float?

  users           UserAchievement[]

  createdAt       DateTime @default(now())
}

model UserAchievement {
  id              String   @id @default(cuid())
  userId          String
  user            User     @relation(fields: [userId], references: [id], onDelete: Cascade)
  achievementId   String
  achievement     Achievement @relation(fields: [achievementId], references: [id], onDelete: Cascade)

  unlockedAt      DateTime @default(now())

  @@unique([userId, achievementId])
  @@index([userId])
}

// =============================================
// ADMIN LOGLARI
// =============================================

model AdminLog {
  id              String   @id @default(cuid())
  adminEmail      String
  action          String
  resourceType    String
  resourceId      String?
  metadata        Json?
  ipAddress       String?

  createdAt       DateTime @default(now())

  @@index([adminEmail])
  @@index([createdAt])
}
```

## Prisma Client Singleton

Development modunda hot-reload her seferinde yeni bağlantı açmasını engellemek için:

```typescript
// src/lib/prisma.ts

import { PrismaClient } from '@prisma/client'

const globalForPrisma = globalThis as unknown as {
  prisma: PrismaClient | undefined
}

export const prisma = globalForPrisma.prisma ?? new PrismaClient({
  log: process.env.NODE_ENV === 'development' ? ['query', 'error', 'warn'] : ['error'],
})

if (process.env.NODE_ENV !== 'production') globalForPrisma.prisma = prisma
```

## Redis Kullanım Alanları

```typescript
// Cache key yapısı
{
  "company:{companyName}":     "JSON şirket verisi",     // TTL: 7 gün
  "rate:{userId}:{endpoint}":  "istek sayısı",           // TTL: 15 dakika
  "transcription:{jobId}":     "status + result",        // TTL: 24 saat
}
```

## Seed Data

```typescript
// prisma/seed.ts — temel başarımlar ve admin kullanıcı
const achievements = [
  { code: 'FIRST_INTERVIEW', name: 'İlk Adım', description: 'İlk mülakatını tamamla', icon: '🎯', category: 'milestone', requiredCount: 1 },
  { code: 'FIVE_INTERVIEWS', name: 'Deneyimli', description: '5 mülakat tamamla', icon: '⭐', category: 'milestone', requiredCount: 5 },
  { code: 'HIGH_SCORER', name: 'Yüksek Puan', description: '90+ skor al', icon: '🏆', category: 'skill', requiredCount: 1, requiredScore: 90 },
  { code: 'STREAK_7', name: '7 Gün Üst Üste', description: '7 gün arka arkaya mülakat yap', icon: '🔥', category: 'consistency', requiredCount: 7 },
]
```
