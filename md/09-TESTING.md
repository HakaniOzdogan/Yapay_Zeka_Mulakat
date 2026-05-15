# 09 — Test Stratejisi

## Genel Yaklaşım

Üç katmanlı test stratejisi: kritik iş mantığı için unit testler, API akışları için integration testler, kullanıcı yolculukları için e2e testler.

## Araçlar

| Araç | Amaç |
|------|------|
| Vitest | Unit + integration testler |
| React Testing Library | Bileşen testleri |
| Playwright | E2E testler |
| MSW (Mock Service Worker) | API mock'lama |

## Kurulum

```bash
npm install -D vitest @vitest/ui jsdom @testing-library/react @testing-library/jest-dom msw playwright
```

```typescript
// vitest.config.ts
import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./tests/setup.ts'],
  },
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
});
```

```typescript
// tests/setup.ts
import '@testing-library/jest-dom';
import { server } from './mocks/server';

beforeAll(() => server.listen());
afterEach(() => server.resetHandlers());
afterAll(() => server.close());
```

## Unit Testler

### AI Fonksiyonları

```typescript
// tests/unit/ai/scribe.test.ts
import { describe, it, expect, vi } from 'vitest';
import { transcribeWithScribe } from '@/lib/ai/scribe';

describe('transcribeWithScribe', () => {
  it('Türkçe transkripsiyon döndürür', async () => {
    const mockAudio = Buffer.from('fake-audio-data');

    global.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: () => Promise.resolve({
        text: 'Merhaba, ben bir yazılım geliştirici olarak çalışıyorum.',
        words: [],
        language_code: 'tr',
      }),
    });

    const result = await transcribeWithScribe(mockAudio, { language: 'tr' });

    expect(result.text).toContain('Merhaba');
    expect(result.language_code).toBe('tr');
    expect(fetch).toHaveBeenCalledWith(
      'https://api.elevenlabs.io/v1/speech-to-text',
      expect.objectContaining({ method: 'POST' })
    );
  });

  it('API hatası fırlatır', async () => {
    global.fetch = vi.fn().mockResolvedValue({ ok: false, status: 429 });

    await expect(
      transcribeWithScribe(Buffer.from(''), {})
    ).rejects.toThrow('Scribe API error: 429');
  });
});
```

### Keyterm Üretimi

```typescript
// tests/unit/ai/keyterms.test.ts
import { describe, it, expect } from 'vitest';
import { getKeytermsForPosition } from '@/lib/ai/keyterms';

describe('getKeytermsForPosition', () => {
  it('Frontend pozisyonu için doğru terimler döndürür', () => {
    const terms = getKeytermsForPosition('Senior Frontend Developer', 'Teknoloji');
    expect(terms).toContain('React');
    expect(terms).toContain('TypeScript');
    expect(terms.length).toBeLessThanOrEqual(100);
  });

  it('Tekrar eden terimleri filtreler', () => {
    const terms = getKeytermsForPosition('Frontend Developer', '');
    const unique = new Set(terms);
    expect(unique.size).toBe(terms.length);
  });
});
```

### DualStreamRecorder

```typescript
// tests/unit/video/dual-stream-recorder.test.ts
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { DualStreamRecorder } from '@/lib/video/dual-stream-recorder';

// MediaStream, MediaRecorder mock'larını tanımla
const mockStream = {
  getVideoTracks: () => [{ getSettings: () => ({ width: 1920, height: 1080 }), addEventListener: vi.fn(), stop: vi.fn() }],
  getAudioTracks: () => [{ stop: vi.fn() }],
  getTracks: () => [],
  addTrack: vi.fn(),
};

beforeEach(() => {
  vi.stubGlobal('navigator', {
    mediaDevices: {
      getUserMedia: vi.fn().mockResolvedValue(mockStream),
      getDisplayMedia: vi.fn().mockResolvedValue(mockStream),
    },
  });

  vi.stubGlobal('MediaRecorder', vi.fn().mockImplementation(() => ({
    start: vi.fn(),
    stop: vi.fn(),
    state: 'inactive',
    ondataavailable: null,
    onstop: null,
  })));

  // Canvas mock
  const canvasMock = {
    getContext: () => ({ drawImage: vi.fn(), fillRect: vi.fn(), fillStyle: '' }),
    captureStream: () => ({ addTrack: vi.fn(), getTracks: () => [] }),
    width: 0, height: 0,
  };
  vi.stubGlobal('document', {
    createElement: vi.fn().mockReturnValue(canvasMock),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    hidden: false,
  });

  vi.stubGlobal('requestAnimationFrame', vi.fn().mockReturnValue(1));
  vi.stubGlobal('cancelAnimationFrame', vi.fn());
});

describe('DualStreamRecorder', () => {
  it('initialize() ekran ve kamera izni alır', async () => {
    const recorder = new DualStreamRecorder();
    await recorder.initialize();

    expect(navigator.mediaDevices.getUserMedia).toHaveBeenCalledWith(
      expect.objectContaining({ video: expect.anything(), audio: expect.anything() })
    );
    expect(navigator.mediaDevices.getDisplayMedia).toHaveBeenCalled();
  });

  it('onTabSwitch callback sekme değişiminde çağrılır', async () => {
    const onTabSwitch = vi.fn();
    const recorder = new DualStreamRecorder({ onTabSwitch });

    await recorder.initialize();

    // visibilitychange event tetikle
    const handler = (document.addEventListener as any).mock.calls.find(
      (call: any[]) => call[0] === 'visibilitychange'
    )?.[1];

    Object.defineProperty(document, 'hidden', { value: true, configurable: true });
    handler?.();

    expect(onTabSwitch).toHaveBeenCalledWith(true);
  });
});
```

## Integration Testler

```typescript
// tests/integration/api/interview.test.ts
import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { createMocks } from 'node-mocks-http';
import { POST as createInterview } from '@/app/api/interview/create/route';

describe('POST /api/interview/create', () => {
  it('Mülakat oluşturur ve id döndürür', async () => {
    const { req } = createMocks({
      method: 'POST',
      body: {
        position: 'Frontend Developer',
        companyName: 'TestŞirket',
        industry: 'Teknoloji',
        experienceLevel: 'mid',
        interviewType: 'MIXED',
        language: 'tr',
      },
    });

    // Session mock
    vi.mock('@/auth', () => ({
      auth: () => Promise.resolve({ user: { id: 'user-1', subscriptionTier: 'FREE', credits: 5 } }),
    }));

    const response = await createInterview(req as any);
    const data = await response.json();

    expect(response.status).toBe(200);
    expect(data.interview.id).toBeDefined();
    expect(data.interview.status).toBe('DRAFT');
  });

  it('Yetkisiz istek 401 döndürür', async () => {
    vi.mock('@/auth', () => ({
      auth: () => Promise.resolve(null),
    }));

    const { req } = createMocks({ method: 'POST', body: {} });
    const response = await createInterview(req as any);

    expect(response.status).toBe(401);
  });
});
```

## E2E Testler (Playwright)

```typescript
// tests/e2e/interview-flow.test.ts
import { test, expect } from '@playwright/test';

test.describe('Mülakat akışı', () => {
  test.beforeEach(async ({ page }) => {
    // Test kullanıcısıyla giriş yap
    await page.goto('/login');
    await page.fill('[name=email]', 'test@example.com');
    await page.fill('[name=password]', 'TestPass123');
    await page.click('[type=submit]');
    await page.waitForURL('/dashboard');
  });

  test('Mülakat oluşturma formu doğrulama çalışır', async ({ page }) => {
    await page.goto('/interview/new');

    // Boş form gönder
    await page.click('[data-testid=create-button]');
    await expect(page.locator('[data-testid=position-error]')).toBeVisible();
  });

  test('Yeni mülakat oluşturulur ve mülakat ekranına geçilir', async ({ page }) => {
    await page.goto('/interview/new');

    await page.fill('[name=position]', 'Frontend Developer');
    await page.fill('[name=companyName]', 'TestŞirket');
    await page.selectOption('[name=industry]', 'Teknoloji');
    await page.selectOption('[name=experienceLevel]', 'mid');

    // Kamera ve ekran izni mocklama (Playwright context)
    await page.context().grantPermissions(['camera', 'microphone']);

    await page.click('[data-testid=create-button]');
    await page.waitForURL(/\/interview\/[a-z0-9]+/);
    await expect(page.locator('[data-testid=question-display]')).toBeVisible();
  });
});
```

```typescript
// playwright.config.ts
import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/e2e',
  use: {
    baseURL: 'http://localhost:3000',
    trace: 'on-first-retry',
    // Kamera/mikrofon izinlerini otomatik ver
    permissions: ['camera', 'microphone'],
    launchOptions: {
      args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'],
    },
  },
  webServer: {
    command: 'npm run dev',
    url: 'http://localhost:3000',
    reuseExistingServer: !process.env.CI,
  },
});
```

## MSW Mock Handlers

```typescript
// tests/mocks/handlers.ts
import { http, HttpResponse } from 'msw';

export const handlers = [
  // ElevenLabs STT mock
  http.post('https://api.elevenlabs.io/v1/speech-to-text', () => {
    return HttpResponse.json({
      text: 'Mock transkript metni',
      words: [],
      language_code: 'tr',
    });
  }),

  // OpenAI mock
  http.post('https://api.openai.com/v1/chat/completions', () => {
    return HttpResponse.json({
      choices: [{ message: { content: 'Bana kendinizden bahseder misiniz?' } }],
    });
  }),
];
```

## npm scripts

```json
// package.json
{
  "scripts": {
    "dev": "next dev",
    "build": "next build",
    "start": "next start",
    "lint": "next lint",
    "test": "vitest run",
    "test:watch": "vitest",
    "test:ui": "vitest --ui",
    "test:e2e": "playwright test",
    "test:e2e:ui": "playwright test --ui",
    "type-check": "tsc --noEmit"
  }
}
```

## Kapsam Hedefleri

| Katman | Hedef | Öncelik |
|--------|-------|---------|
| AI fonksiyonları (STT, soru üretme) | %90+ | Yüksek |
| API route'ları (auth, mülakat akışı) | %80+ | Yüksek |
| DualStreamRecorder | %70+ | Orta |
| React bileşenleri | %60+ | Orta |
| E2E kritik yolculuklar | 3 senaryo | Yüksek |
