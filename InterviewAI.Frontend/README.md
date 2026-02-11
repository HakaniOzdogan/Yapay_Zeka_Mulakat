# InterviewAI Frontend - Blazor WebAssembly

Modern ve kullanıcı dostu yapay zeka destekli mülakat hazırlık sistemi frontend'i.

## 🎨 Özellikleri

### Sayfa Yapısı
- **Home** (`/`) - Hoş geldiniz sayfa si ve tanıtım
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

## 🚀 Başlangıç

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

## 📁 Proje Yapısı

```
InterviewAI.Frontend/
├── Pages/                 # Blazor sayfaları
│   ├── Home.razor        # Ana sayfa
│   ├── Register.razor    # Kayıt
│   ├── Login.razor       # Giriş
│   ├── Dashboard.razor   # Dashboard
│   ├── InterviewStart.razor      # Mülakat başlat
│   ├── InterviewSession.razor    # Mülakat oturumu
│   └── InterviewFeedback.razor   # Geribildirim
├── Services/             # API servisleri
│   └── ApiService.cs     # Backend komunikasyonu
├── Layout/               # Layout bileşenleri
├── wwwroot/              # Static files
│   ├── css/
│   │   ├── app.css       # Bootstrap styles
│   │   └── custom.css    # Custom utilities
│   ├── js/
│   ├── lib/
│   └── index.html        # HTML giriş noktası
├── App.razor             # Kök bileşen
├── Program.cs            # Başlangıç noktası
├── _Imports.razor        # Global using'ler
└── InterviewAI.Frontend.csproj

```

## 🔌 API Entegrasyonu

Frontend, backend API'ını şu address'te çağırır:
- **Base URL**: `https://localhost:7182/api`

### API Endpoints

#### Users
- `GET /api/users`
- `POST /api/users`
- `GET /api/users/{id}`
- `PUT /api/users/{id}`
- `DELETE /api/users/{id}`

#### Interviews
- `GET /api/interviews`
- `GET /api/interviews/user/{userId}`
- `POST /api/interviews`
- `PUT /api/interviews/{id}`
- `DELETE /api/interviews/{id}`

#### Feedback
- `GET /api/feedback`
- `GET /api/feedback/interview/{interviewId}`
- `POST /api/feedback`

## 🎯 Kullanıcı Akışı

1. **Giriş**: Home sayfasından başlayıp Register/Login'e gidin
2. **Dashboard**: Mülakatlarını görmek ve yeni başlatmak
3. **Mülakat Başlat**: Mülakat türü, pozisyon ve zorluk seviyesi seçim
4. **Mülakat Yap**: Zaman sınırı ile sorulara cevap verin
5. **Geribildirim**: AI analizi ve iyileştirme önerileri

## 🎨 Stil Mimarisi

- **Bootstrap 5**: Temel bileşenler
- **Custom Utilities**: Tailwind-like utility classes
- **CSS Variables**: Renk ve tema yönetimi
- **Responsive Design**: Mobile-first approach

## 📊 UI Components

### Buttons
```html
<button class="btn btn-primary">Birincil Duğme</button>
<button class="btn btn-success">Başarı Duğmesi</button>
```

### Cards
```html
<div class="card">
    <div class="card-body">
        <h5 class="card-title">Başlık</h5>
        <p class="card-text">İçerik</p>
    </div>
</div>
```

### Forms
```html
<input type="text" class="form-control" placeholder="Metin gir">
<textarea class="form-control" rows="4"></textarea>
<select class="form-select">
    <option>Seçenek 1</option>
    <option>Seçenek 2</option>
</select>
```

## 🔐 Güvenlik Notları

- CORS ayarlarını backend'de kontrol edin
- HttpClient SSL sertifikasını doğrulayın
- TokenAuth/JWT entegrasyonunu sonra ekleyin
- Hassas verileri localStorage'a kaydetmeyin

## 🐛 Troubleshooting

### CORS Hatası
Backend'de CORS policy'si ayarlandığından emin olun:
```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});
```

### API Bağlantı Hatası
- Backend'in çalıştığını doğrulayın
- `ApiService.cs`'de doğru base URL kullandığını kontrol edin
- Browser console'da hata mesajlarını kontrol edin

## 📱 Responsive Design

- **Tablet (768px+)**: 2-col grid
- **Desktop (1024px+)**: Full layouts
- **Mobile**: Single column, optimized buttons

## 🚀 Deployment

### Azure App Service
```bash
dotnet publish -c Release -o ./publish
```

### Docker
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10 AS build
WORKDIR /src
COPY ["InterviewAI.Frontend.csproj", "."]
RUN dotnet restore "InterviewAI.Frontend.csproj"
COPY . .
RUN dotnet build "InterviewAI.Frontend.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "InterviewAI.Frontend.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "InterviewAI.Frontend.dll"]
```

## 📝 Geliştirilecek Alanlar

- [ ] Test coverage
- [ ] Accessibility (A11y) improvements
- [ ] Performance optimization
- [ ] Offline support (PWA)
- [ ] Dark mode
- [ ] Advanced analytics dashboard
- [ ] AI model integration
- [ ] Real-time notifications

## 👥 Katkı

Katkıda bulunan olabilir ve PR göndere
bilirsiniz.

## 📄 Lisans

MIT License - Eğitim Projesi
