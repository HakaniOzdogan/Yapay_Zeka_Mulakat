> ⚠️ **ARŞİV:** Bu klasör artık aktif değildir. Blazor WebAssembly + .NET 10 tabanlı eski prototip frontend'ini içerir.
>
> Aktif frontend `src/frontend/` dizinindedir: React 18 + TypeScript + Vite.
> Güncel mimari için bkz. [`docs/architecture.md`](../docs/architecture.md).

---

# InterviewAI Frontend - Blazor WebAssembly (Prototip)

Modern ve kullanıcı dostu yapay zeka destekli mülakat hazırlık sistemi frontend'i.

## Özellikleri

### Sayfa Yapısı
- **Home** (`/`) - Hoş geldiniz sayfası ve tanıtım
- **Register** (`/register`) - Kullanıcı kayıt formu
- **Login** (`/login`) - Kullanıcı giriş
- **Dashboard** (`/dashboard`) - Ana pano ve mülakatlar özeti
- **Interview Start** (`/interview`) - Mülakat türü seçim ekranı
- **Interview Session** (`/interview-session/{id}`) - Mülakat simülasyonu (sorular ve cevaplar)
- **Interview Feedback** (`/interview-feedback/{id}`) - Detaylı geribildirim ve analiz

### Teknoloji Yığını
- **Framework**: Blazor WebAssembly (.NET 10)
- **Styling**: Bootstrap 5 + Custom CSS
- **HTTP Client**: HttpClient for API communication
- **Language**: C# with Razor components

### UI Bileşenleri
- Responsive navbar
- Modern gradient backgrounds
- Form validasyonu
- Progress bars ve timers
- Card-based layouts
- Grid and flex utilities
- Hover effects ve animations

## Başlangıç

### Gereksinimler
- .NET 10 SDK
- ASP.NET Core Backend (InterviewAI.API)

### Kurulum

1. Backend'in çalıştığından emin olun:
```bash
cd ../InterviewAI
dotnet run
```

2. Frontend'i çalıştırın:
```bash
dotnet run
```

Frontend, `https://localhost:5001` veya `https://localhost:7001` adresinde açılacaktır.

### Geliştirme Modu
```bash
dotnet watch run
```

## Proje Yapısı

```
InterviewAI.Frontend/
├── Pages/
│   ├── Home.razor
│   ├── Register.razor
│   ├── Login.razor
│   ├── Dashboard.razor
│   ├── InterviewStart.razor
│   ├── InterviewSession.razor
│   └── InterviewFeedback.razor
├── Services/
│   └── ApiService.cs
├── Layout/
├── wwwroot/
│   ├── css/
│   │   ├── app.css
│   │   └── custom.css
│   ├── js/
│   ├── lib/
│   └── index.html
├── App.razor
├── Program.cs
├── _Imports.razor
└── InterviewAI.Frontend.csproj
```

## Güvenlik Notları

- CORS ayarlarını backend'de kontrol edin
- HttpClient SSL sertifikasını doğrulayın
- TokenAuth/JWT entegrasyonunu sonra ekleyin
- Hassas verileri localStorage'a kaydetmeyin

## Geliştirilecek Alanlar

- [ ] Test coverage
- [ ] Accessibility (A11y) improvements
- [ ] Performance optimization
- [ ] Offline support (PWA)
- [ ] Dark mode
- [ ] Advanced analytics dashboard
- [ ] AI model integration
- [ ] Real-time notifications

## Lisans

MIT License - Eğitim Projesi
