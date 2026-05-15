# 06 — Authentication ve Güvenlik

## Genel Bakış

Kimlik doğrulama NextAuth.js v5 ile yönetilir. JWT tabanlı session stratejisi kullanılır — veritabanı session tablosu yerine imzalı JWT token'lar tercih edilir (Vercel Edge uyumluluğu için).

## NextAuth Konfigürasyonu

```typescript
// src/auth.ts

import NextAuth from 'next-auth';
import { PrismaAdapter } from '@auth/prisma-adapter';
import CredentialsProvider from 'next-auth/providers/credentials';
import GoogleProvider from 'next-auth/providers/google';
import bcrypt from 'bcryptjs';
import { prisma } from '@/lib/prisma';
import { z } from 'zod';

export const { handlers, auth, signIn, signOut } = NextAuth({
  adapter: PrismaAdapter(prisma),
  session: { strategy: 'jwt', maxAge: 30 * 24 * 60 * 60 },  // 30 gün

  providers: [
    GoogleProvider({
      clientId: process.env.GOOGLE_CLIENT_ID!,
      clientSecret: process.env.GOOGLE_CLIENT_SECRET!,
    }),

    CredentialsProvider({
      name: 'credentials',
      credentials: {
        email: { label: 'Email', type: 'email' },
        password: { label: 'Şifre', type: 'password' },
      },
      async authorize(credentials) {
        const parsed = z.object({
          email: z.string().email(),
          password: z.string().min(8),
        }).safeParse(credentials);

        if (!parsed.success) return null;

        const user = await prisma.user.findUnique({
          where: { email: parsed.data.email },
        });

        if (!user?.password) return null;

        const isValid = await bcrypt.compare(parsed.data.password, user.password);
        if (!isValid) return null;

        return {
          id: user.id,
          email: user.email,
          name: user.name,
          image: user.image,
          subscriptionTier: user.subscriptionTier,
          credits: user.credits,
        };
      },
    }),
  ],

  callbacks: {
    async jwt({ token, user, trigger, session }) {
      if (user) {
        token.id = user.id;
        token.subscriptionTier = (user as any).subscriptionTier;
        token.credits = (user as any).credits;
      }

      // "update" trigger: session.update() çağrıldığında token'ı tazele
      if (trigger === 'update' && session) {
        const fresh = await prisma.user.findUnique({
          where: { id: token.id as string },
          select: { subscriptionTier: true, credits: true },
        });
        if (fresh) {
          token.subscriptionTier = fresh.subscriptionTier;
          token.credits = fresh.credits;
        }
      }

      return token;
    },

    async session({ session, token }) {
      session.user.id = token.id as string;
      session.user.subscriptionTier = token.subscriptionTier as string;
      session.user.credits = token.credits as number;
      return session;
    },
  },

  pages: {
    signIn: '/login',
    error: '/login',
  },
});
```

## NextAuth Route Handler

```typescript
// src/app/api/auth/[...nextauth]/route.ts

import { handlers } from '@/auth';
export const { GET, POST } = handlers;
```

## Middleware — Route Koruması

```typescript
// src/middleware.ts

import { auth } from '@/auth';
import { NextResponse } from 'next/server';

export default auth((req) => {
  const isAuthenticated = !!req.auth;
  const path = req.nextUrl.pathname;

  // Korumalı route'lar
  const protectedPrefixes = ['/dashboard', '/interview', '/report', '/profile'];
  const isProtected = protectedPrefixes.some(p => path.startsWith(p));

  if (isProtected && !isAuthenticated) {
    const loginUrl = new URL('/login', req.url);
    loginUrl.searchParams.set('callbackUrl', path);
    return NextResponse.redirect(loginUrl);
  }

  // Admin route'ları
  if (path.startsWith('/admin') && req.auth?.user?.email !== process.env.ADMIN_EMAIL) {
    return NextResponse.redirect(new URL('/dashboard', req.url));
  }

  return NextResponse.next();
});

export const config = {
  matcher: [
    '/((?!api/auth|_next/static|_next/image|favicon.ico|public).*)',
  ],
};
```

## TypeScript Tip Genişletme

```typescript
// src/types/next-auth.d.ts

import { DefaultSession } from 'next-auth';

declare module 'next-auth' {
  interface Session {
    user: DefaultSession['user'] & {
      id: string;
      subscriptionTier: string;
      credits: number;
    };
  }
}
```

## API Route Koruması

Her API route'unda session kontrolü için utility:

```typescript
// src/lib/api/auth-guard.ts

import { auth } from '@/auth';
import { NextRequest, NextResponse } from 'next/server';

type ApiHandler = (
  req: NextRequest,
  context: { params: any; user: { id: string; subscriptionTier: string; credits: number } }
) => Promise<NextResponse>;

export function withAuth(handler: ApiHandler) {
  return async (req: NextRequest, context: { params: any }) => {
    const session = await auth();

    if (!session?.user?.id) {
      return NextResponse.json({ error: 'Unauthorized' }, { status: 401 });
    }

    return handler(req, {
      ...context,
      user: {
        id: session.user.id,
        subscriptionTier: session.user.subscriptionTier,
        credits: session.user.credits,
      },
    });
  };
}

// Kullanım:
// export const GET = withAuth(async (req, { user }) => { ... });
```

## Rate Limiting

```typescript
// src/lib/api/rate-limit.ts

import { Redis } from '@upstash/redis';
import { Ratelimit } from '@upstash/ratelimit';

const redis = Redis.fromEnv();

export const rateLimiters = {
  // Genel API: 100 istek / 15 dakika
  general: new Ratelimit({
    redis,
    limiter: Ratelimit.slidingWindow(100, '15 m'),
    prefix: 'rl:general',
  }),

  // AI endpoint'leri: 10 istek / 1 dakika
  ai: new Ratelimit({
    redis,
    limiter: Ratelimit.slidingWindow(10, '1 m'),
    prefix: 'rl:ai',
  }),

  // Auth: 5 istek / 15 dakika
  auth: new Ratelimit({
    redis,
    limiter: Ratelimit.slidingWindow(5, '15 m'),
    prefix: 'rl:auth',
  }),

  // Upload: 20 istek / 1 saat
  upload: new Ratelimit({
    redis,
    limiter: Ratelimit.slidingWindow(20, '1 h'),
    prefix: 'rl:upload',
  }),
};

export async function checkRateLimit(
  limiter: Ratelimit,
  identifier: string
): Promise<NextResponse | null> {
  const { success, limit, remaining, reset } = await limiter.limit(identifier);

  if (!success) {
    return NextResponse.json(
      { error: 'Too Many Requests', retryAfter: Math.ceil((reset - Date.now()) / 1000) },
      {
        status: 429,
        headers: {
          'X-RateLimit-Limit': limit.toString(),
          'X-RateLimit-Remaining': remaining.toString(),
          'Retry-After': Math.ceil((reset - Date.now()) / 1000).toString(),
        },
      }
    );
  }

  return null;
}
```

## Şifre Güvenliği

```typescript
// src/app/api/auth/register/route.ts

import bcrypt from 'bcryptjs';
import { z } from 'zod';

const RegisterSchema = z.object({
  email: z.string().email('Geçerli bir email adresi girin'),
  password: z
    .string()
    .min(8, 'Şifre en az 8 karakter olmalı')
    .regex(/[A-Z]/, 'En az bir büyük harf içermeli')
    .regex(/[0-9]/, 'En az bir rakam içermeli'),
  name: z.string().min(2, 'Ad en az 2 karakter olmalı').max(50),
});

export async function POST(req: NextRequest) {
  // Rate limit
  const rateLimitResponse = await checkRateLimit(
    rateLimiters.auth,
    req.headers.get('x-forwarded-for') ?? 'unknown'
  );
  if (rateLimitResponse) return rateLimitResponse;

  const body = await req.json();
  const parsed = RegisterSchema.safeParse(body);

  if (!parsed.success) {
    return NextResponse.json(
      { error: parsed.error.errors[0].message },
      { status: 400 }
    );
  }

  const { email, password, name } = parsed.data;

  // Email teklik kontrolü
  const existing = await prisma.user.findUnique({ where: { email } });
  if (existing) {
    return NextResponse.json(
      { error: 'Bu email adresi zaten kullanılıyor' },
      { status: 409 }
    );
  }

  // Şifreyi hashle (bcrypt, salt rounds: 12)
  const hashedPassword = await bcrypt.hash(password, 12);

  const user = await prisma.user.create({
    data: { email, password: hashedPassword, name },
  });

  return NextResponse.json({ id: user.id }, { status: 201 });
}
```

## Güvenlik Kontrol Listesi

- [x] `.env.local` `.gitignore`'da — asla commit'lenmesin
- [x] `NEXTAUTH_SECRET` en az 32 karakter rastgele string
- [x] `DATABASE_URL` production'da SSL zorunlu (`?sslmode=require`)
- [x] Tüm API route'larında `withAuth` wrapper
- [x] Tüm AI endpoint'lerinde rate limiting
- [x] Şifreler bcrypt ile hash (salt: 12)
- [x] Prisma parametrize sorguları (SQL injection önlemi)
- [x] CORS: sadece `FRONTEND_URL` origin'ine izin
- [x] Content Security Policy header'ları
- [x] Video/ses dosyaları S3'te private (presigned URL ile erişim)
- [x] KVKK: kullanıcı silme endpoint'i mevcut
- [x] Admin route'ları ayrı yetki kontrolü
