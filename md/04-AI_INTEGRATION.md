# 04 — AI Entegrasyonları

## Genel Bakış

Platform üç ana AI servisi kullanır:

| Servis | Sağlayıcı | Amaç | Maliyet |
|--------|-----------|------|---------|
| Soru üretme / Cevap değerlendirme | OpenAI GPT-4 Turbo | Adaptif sorular, içerik analizi | ~$0.01-0.03/soru |
| Konuşmadan yazıya (STT) | ElevenLabs Scribe v2 Batch | Ses → transkript | ~$0.0037/dk ($0.22/saat) |
| Ses analizi | Python librosa | Pitch, tempo, duraksamalar | Kendi sunucu (maliyet yok) |

## 1. Konuşmadan Yazıya — ElevenLabs Scribe v2 Batch

### Neden Scribe v2?

Orijinal tasarımda Whisper API ile 3 sn'lik chunk'lar halinde real-time transkripsiyon yapılıyordu. Bu yaklaşım:
- Düşük doğruluk veriyordu (cümle ortasında kesme)
- Pahalıydı (gereksiz fazla API çağrısı)
- Kullanıcıyı rahatsız eden live transcript UX'i yaratıyordu

Yeni yaklaşım: Kullanıcı cevabını bitirir → tüm ses bir kerede Scribe v2 Batch'e gönderilir → arka planda transkript oluşur → raporda kullanılır.

### Model karşılaştırması (karar gerekçesi)

| Model | Türkçe WER | Fiyat/dk | Batch? | Keyterm? |
|-------|-----------|----------|--------|----------|
| ElevenLabs Scribe v2 Batch | ≤%5 | $0.0037 | Evet | Evet (1000 terim) |
| Groq Whisper large-v3 | ~%8.4 | ~$0.002 | Evet | Hayır |
| OpenAI Whisper API | ~%7.6 | $0.006 | Evet | Hayır |
| Deepgram Nova-2 | Zayıf (Türkçe) | $0.0043 | Evet | Hayır |

### Entegrasyon kodu

```typescript
// src/lib/ai/scribe.ts

interface ScribeTranscriptionResult {
  text: string;
  words: Array<{
    word: string;
    start: number;
    end: number;
    confidence: number;
  }>;
  language_code: string;
}

export async function transcribeWithScribe(
  audioBuffer: Buffer,
  options: {
    language?: 'tr' | 'en';
    keyterms?: string[];           // Pozisyona özel teknik terimler
    noVerbatim?: boolean;          // Dolgu kelimeleri kaldır
  } = {}
): Promise<ScribeTranscriptionResult> {
  const formData = new FormData();
  formData.append('file', new Blob([audioBuffer]), 'audio.webm');
  formData.append('model_id', 'scribe_v2');

  if (options.language) {
    formData.append('language_code', options.language);
  }

  // Keyterm prompting: pozisyona özel terimler
  // Örn: Frontend Developer → ["React", "TypeScript", "Next.js", "Webpack"]
  if (options.keyterms && options.keyterms.length > 0) {
    formData.append('keyterms', JSON.stringify(
      options.keyterms.slice(0, 1000).map(term => ({
        text: term.slice(0, 50)
      }))
    ));
  }

  if (options.noVerbatim) {
    formData.append('no_verbatim', 'true');
  }

  const response = await fetch('https://api.elevenlabs.io/v1/speech-to-text', {
    method: 'POST',
    headers: {
      'xi-api-key': process.env.ELEVENLABS_API_KEY!,
    },
    body: formData,
  });

  if (!response.ok) {
    throw new Error(`Scribe API error: ${response.status}`);
  }

  return response.json();
}
```

### Keyterm prompting stratejisi

Mülakat oluşturulurken pozisyon ve sektöre göre otomatik terimler eklenir:

```typescript
// src/lib/ai/keyterms.ts

const KEYTERM_MAP: Record<string, string[]> = {
  'frontend': ['React', 'Vue', 'Angular', 'TypeScript', 'JavaScript', 'CSS', 'Webpack', 'Vite', 'Next.js', 'Tailwind', 'Redux', 'Zustand', 'REST API', 'GraphQL', 'responsive', 'accessibility', 'WCAG', 'Figma'],
  'backend': ['Node.js', 'Express', 'NestJS', 'FastAPI', 'Django', 'PostgreSQL', 'MongoDB', 'Redis', 'Docker', 'Kubernetes', 'microservices', 'REST', 'GraphQL', 'CI/CD', 'AWS', 'GCP'],
  'data-science': ['Python', 'TensorFlow', 'PyTorch', 'pandas', 'scikit-learn', 'SQL', 'ETL', 'machine learning', 'deep learning', 'NLP', 'regression', 'classification', 'A/B test'],
  'product-manager': ['roadmap', 'sprint', 'backlog', 'OKR', 'KPI', 'user story', 'MVP', 'A/B test', 'retention', 'churn', 'NPS', 'AARRR', 'Jira', 'agile', 'scrum'],
  'finans': ['EBITDA', 'ROI', 'ROE', 'bilanço', 'gelir tablosu', 'nakit akışı', 'WACC', 'DCF', 'NPV', 'UFRS', 'TFRS', 'konsolidasyon', 'denetim'],
};

export function getKeytermsForPosition(position: string, industry: string): string[] {
  const posLower = position.toLowerCase();
  const terms: string[] = [];

  for (const [key, values] of Object.entries(KEYTERM_MAP)) {
    if (posLower.includes(key)) {
      terms.push(...values);
    }
  }

  // Sektöre göre ek terimler
  if (industry) {
    terms.push(industry);
  }

  return [...new Set(terms)].slice(0, 100);
}
```

### Transkripsiyon akış yönetimi

```typescript
// src/lib/ai/transcription-manager.ts

import { prisma } from '@/lib/prisma';
import { transcribeWithScribe } from './scribe';
import { getKeytermsForPosition } from './keyterms';

export async function processTranscription(
  responseId: string,
  audioUrl: string,
  interviewId: string
) {
  // 1. Audio dosyasını S3'ten çek
  const audioBuffer = await downloadFromS3(audioUrl);

  // 2. Mülakat bilgilerini al (keyterm için)
  const interview = await prisma.interview.findUnique({
    where: { id: interviewId },
    select: { position: true, industry: true, language: true }
  });

  // 3. Keyterm'leri hazırla
  const keyterms = getKeytermsForPosition(
    interview!.position,
    interview!.industry
  );

  // 4. Status güncelle
  await prisma.interviewResponse.update({
    where: { id: responseId },
    data: { transcriptionStatus: 'PROCESSING' }
  });

  try {
    // 5. Scribe v2 Batch'e gönder
    const result = await transcribeWithScribe(audioBuffer, {
      language: interview!.language as 'tr' | 'en',
      keyterms,
      noVerbatim: false,  // Dolgu kelimeleri analiz için tut
    });

    // 6. Sonucu kaydet
    await prisma.interviewResponse.update({
      where: { id: responseId },
      data: {
        transcription: result.text,
        transcriptionStatus: 'COMPLETED',
      }
    });

    return result;
  } catch (error) {
    await prisma.interviewResponse.update({
      where: { id: responseId },
      data: { transcriptionStatus: 'FAILED' }
    });
    throw error;
  }
}
```

## 2. Soru Üretme — OpenAI GPT-4 Turbo

```typescript
// src/lib/ai/question-generator.ts

import OpenAI from 'openai';

const openai = new OpenAI({ apiKey: process.env.OPENAI_API_KEY });

export async function generateInterviewQuestion(params: {
  interviewId: string;
  questionNumber: number;
  totalQuestions: number;
  position: string;
  industry: string;
  companyName: string;
  companyData?: any;
  previousAnswers: string[];
  interviewType: string;
  language: string;
}) {
  const { questionNumber, totalQuestions, position, industry, companyName, companyData, previousAnswers, interviewType, language } = params;

  const systemPrompt = language === 'tr'
    ? `Sen profesyonel bir İK mülakatçısısın. ${position} pozisyonu için ${industry} sektöründe ${companyName} şirketinde mülakat yapıyorsun.

Kurallar:
1. Önceki cevaplara göre adaptif sorular sor
2. STAR metodunu teşvik eden açık uçlu sorular sor
3. Teknik ve davranışsal soruları dengele
4. Şirket kültürüne uygun sorular sor
5. Zorluk seviyesi: ${questionNumber <= Math.ceil(totalQuestions * 0.3) ? 'kolay-orta (ısınma)' : questionNumber <= Math.ceil(totalQuestions * 0.7) ? 'orta' : 'orta-zor'}
6. Bu ${questionNumber}/${totalQuestions}. soru

Şirket bilgileri: ${companyData ? JSON.stringify(companyData) : 'Bilinmiyor'}

Sadece soru metnini yaz, başka bir şey ekleme.`
    : `You are a professional HR interviewer conducting an interview for the ${position} position in the ${industry} sector at ${companyName}.

Rules:
1. Ask adaptive questions based on previous answers
2. Ask open-ended questions that encourage the STAR method
3. Balance technical and behavioral questions
4. Ask questions aligned with company culture
5. Difficulty: ${questionNumber <= Math.ceil(totalQuestions * 0.3) ? 'easy-medium (warmup)' : questionNumber <= Math.ceil(totalQuestions * 0.7) ? 'medium' : 'medium-hard'}
6. This is question ${questionNumber}/${totalQuestions}

Company info: ${companyData ? JSON.stringify(companyData) : 'Unknown'}

Write only the question text, nothing else.`;

  const userContent = previousAnswers.length > 0
    ? `Önceki cevaplar:\n${previousAnswers.map((a, i) => `${i + 1}. ${a}`).join('\n')}\n\nŞimdi sonraki soruyu sor.`
    : 'İlk soruyu sor. Kendini tanıtma, doğrudan soruya geç.';

  const completion = await openai.chat.completions.create({
    model: 'gpt-4-turbo',
    messages: [
      { role: 'system', content: systemPrompt },
      { role: 'user', content: userContent }
    ],
    temperature: 0.8,
    max_tokens: 300,
  });

  return completion.choices[0].message.content!.trim();
}
```

## 3. Cevap Değerlendirme — GPT-4

Her soru cevabı için içerik analizi yapılır. Bu, mülakat bittikten sonra batch olarak çalışır.

```typescript
// src/lib/ai/answer-evaluator.ts

export async function evaluateAnswer(params: {
  question: string;
  transcript: string;
  expectedKeywords: string[];
  position: string;
  language: string;
}): Promise<AnswerEvaluation> {
  const completion = await openai.chat.completions.create({
    model: 'gpt-4-turbo',
    messages: [
      {
        role: 'system',
        content: `Sen bir mülakat değerlendirme uzmanısın. Cevabı şu kriterlere göre 0-10 arası puanla:
1. relevance: Cevap soruyla alakalı mı?
2. depth: Yeterince detaylı mı?
3. structure: STAR metodunu kullanmış mı?
4. examples: Somut örnekler var mı?
5. keywords: Teknik terimleri doğru kullanmış mı?

Ayrıca kısa strengths (güçlü yönler) ve improvements (gelişim alanları) listesi ver.

JSON formatında dön: { relevance, depth, structure, examples, keywords, strengths: [], improvements: [] }`
      },
      {
        role: 'user',
        content: `Soru: ${params.question}\nCevap: ${params.transcript}\nBeklenen anahtar kelimeler: ${params.expectedKeywords.join(', ')}`
      }
    ],
    response_format: { type: 'json_object' },
    temperature: 0.3,
    max_tokens: 500,
  });

  return JSON.parse(completion.choices[0].message.content!);
}
```

## 4. Rapor Oluşturma — GPT-4

Mülakat bittikten sonra tüm veriler birleştirilerek kapsamlı bir rapor oluşturulur.

```typescript
// src/lib/ai/report-generator.ts

export async function generateInterviewReport(interviewId: string) {
  // 1. Tüm verileri topla
  const interview = await prisma.interview.findUnique({
    where: { id: interviewId },
    include: {
      questions: true,
      responses: {
        include: { question: true }
      }
    }
  });

  // 2. Tüm transkriptlerin tamamlanmasını bekle
  const pendingTranscripts = interview!.responses.filter(
    r => r.transcriptionStatus !== 'COMPLETED'
  );
  if (pendingTranscripts.length > 0) {
    // Polling ile bekle (max 5 dakika)
    await waitForTranscriptions(pendingTranscripts.map(r => r.id));
  }

  // 3. GPT-4 ile kapsamlı rapor oluştur
  const reportData = await openai.chat.completions.create({
    model: 'gpt-4-turbo',
    messages: [
      {
        role: 'system',
        content: `Sen bir mülakat değerlendirme uzmanısın. Verilen mülakat verilerine göre detaylı bir analiz raporu oluştur.

JSON formatında dön:
{
  overallScore: 0-100,
  overallFeedback: "2-3 paragraf genel değerlendirme",
  communicationScore: 0-100,
  technicalScore: 0-100,
  confidenceScore: 0-100,
  clarityScore: 0-100,
  bodyLanguageScore: 0-100,
  responseRelevance: 0-100,
  strengths: ["güçlü yön 1", "güçlü yön 2", ...],
  weaknesses: ["zayıf yön 1", ...],
  improvements: ["öneri 1", "öneri 2", ...],
  questionFeedback: [{ questionId, score, feedback, strengths, improvements }],
  aiSummary: "3-4 paragraf detaylı özet"
}`
      },
      {
        role: 'user',
        content: JSON.stringify({
          position: interview!.position,
          company: interview!.companyName,
          questions: interview!.responses.map(r => ({
            question: r.question.questionText,
            answer: r.transcription,
            duration: r.duration,
            audioAnalysis: r.audioAnalysis,
            faceAnalysis: r.faceAnalysis,
          }))
        })
      }
    ],
    response_format: { type: 'json_object' },
    temperature: 0.3,
    max_tokens: 4000,
  });

  // 4. Veritabanına kaydet
  const report = JSON.parse(reportData.choices[0].message.content!);

  await prisma.interviewAnalysis.create({
    data: {
      interviewId,
      ...report,
    }
  });

  // 5. Interview status güncelle
  await prisma.interview.update({
    where: { id: interviewId },
    data: {
      status: 'COMPLETED',
      overallScore: report.overallScore,
    }
  });

  return report;
}
```

## Maliyet Hesabı (Tahmini)

Tek bir 10 soruluk mülakat için:

| Servis | Kullanım | Tahmini Maliyet |
|--------|---------|----------------|
| GPT-4 Turbo (soru üretme) | 10 soru × ~300 token | ~$0.15 |
| GPT-4 Turbo (cevap değerlendirme) | 10 cevap × ~500 token | ~$0.25 |
| GPT-4 Turbo (rapor) | 1 rapor × ~4000 token | ~$0.10 |
| ElevenLabs Scribe v2 | ~20 dk ses | ~$0.07 |
| **Toplam** | | **~$0.57/mülakat** |
