# Interview AI - Yapay Zeka Destekli Mülakat Sistemi

Günümüzde yapay zeka teknolojisini kullanarak interaktif mülakat pratik platformu. Kullanıcılar bu sistem üzerinden mülakat yapabilir, AI botundan geribildirim alabilir ve mülakatlarındaki eksiklikleri ve gelişim alanlarını tespit edebilirler.

## 🎯 Sistem Mimarisi

### Backend - ASP.NET Core Web API
- **Framework**: ASP.NET Core 10.0
- **Database**: SQL Server (LocalDB)
- **ORM**: Entity Framework Core
- **API Documentation**: Swagger/OpenAPI

### Veritabanı Modelleri

#### User (Kullanıcı)
- Temel kullanıcı bilgileri
- CV/Resume içeriği
- Deneyim seviyesi
- Mülakatlar ve ilerleme kaydı

#### Interview (Mülakat)
- Mülakat türü (İş pozisyonu, alan)
- Zorluk seviyesi
- Soru sayısı ve doğru cevap sayısı
- Genel skor ve süre
- Status (Devam ediyor, Tamamlandı, Terk edildi)

#### InterviewQuestion (Mülakat Sorusu)
- Soru metni
- Kullanıcı cevabı ve ideal cevap
- Soru türü (Teknik, Davranışsal, HR vb.)
- Verilen puan
- Harcanan zaman

#### Feedback (Geribildirim)
- Kategori bazlı feedback
- Ciddiyeti seviyesi
- İyileştirme önerileri

#### QuestionFeedback (Soru Bazlı Geribildirim)
- Güçlü yönler
- Geliştirilecek alanlar
- Geliştirme önerileri

#### UserProgress (Kullanıcı İlerlemesi)
- Metrik bazlı ilerleme takibi
- Ortalama başarı oranı
- Zayıf alanlar

## 🚀 Başlangıç

### Gereksinimler
- .NET 10.0 SDK
- SQL Server (LocalDB)
- Visual Studio Code veya Visual Studio

### Kurulum

1. Projeyi klonlayın:
```bash
cd InterviewAI
```

2. NuGet paketlerini geri yükleyin:
```bash
dotnet restore
```

3. Veritabanı migrasyonunu çalıştırın:
```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

4. Projeyi çalıştırın:
```bash
dotnet run
```

Swagger UI'ye erişmek için: `https://localhost:5001/swagger/index.html`

## 📊 API Endpoints

### Users
- `GET /api/users` - Tüm kullanıcıları al
- `GET /api/users/{id}` - Belirli kullanıcıyı al
- `POST /api/users` - Yeni kullanıcı oluştur
- `PUT /api/users/{id}` - Kullanıcı güncelle
- `DELETE /api/users/{id}` - Kullanıcı sil

### Interviews
- `GET /api/interviews` - Tüm mülakatları al
- `GET /api/interviews/{id}` - Belirli mülakatı al
- `GET /api/interviews/user/{userId}` - Kullanıcının mülakatlarını al
- `POST /api/interviews` - Yeni mülakat oluştur
- `PUT /api/interviews/{id}` - Mülakat güncelle
- `DELETE /api/interviews/{id}` - Mülakat sil

### Feedback
- `GET /api/feedback` - Tüm feedbackleri al
- `GET /api/feedback/{id}` - Belirli feedbacki al
- `GET /api/feedback/interview/{interviewId}` - Mülakat feedbacklerini al
- `POST /api/feedback` - Yeni feedback oluştur
- `PUT /api/feedback/{id}` - Feedback güncelle
- `DELETE /api/feedback/{id}` - Feedback sil

## 🏗️ Proje Yapısı

```
InterviewAI/
├── Controllers/          # API Controller'ları
│   ├── UsersController.cs
│   ├── InterviewsController.cs
│   └── FeedbackController.cs
├── Models/              # Domain modelleri
│   ├── User.cs
│   ├── Interview.cs
│   ├── InterviewQuestion.cs
│   ├── Feedback.cs
│   ├── QuestionFeedback.cs
│   └── UserProgress.cs
├── Data/                # Entity Framework DbContext
│   └── ApplicationDbContext.cs
├── Program.cs           # Uygulama başlangıç noktası
├── appsettings.json     # Konfigürasyon
└── InterviewAI.csproj   # Proje dosyası
```

## 🔄 Sonraki Adımlar

1. **AI Bot Entegrasyonu**: ChatGPT API veya Azure OpenAI integrasyon
2. **Frontend Geliştirme**: React/Angular/Blazor UI
3. **Mülakat Analiz Motoru**: Otomatik cevap değerlendirmesi
4. **Raporlama Sistemi**: Detaylı ilerleme ve analytics
5. **Authentication**: JWT-based kullanıcı kimlik doğrulama
6. **Notification Sistemi**: Real-time bildirimler

## 📝 Lisans

Bu proje eğitim amaçlı geliştirilmiştir.

## 👤 Geliştirici

Mülakat Hazırlık Sistemi Projesi - 2026
