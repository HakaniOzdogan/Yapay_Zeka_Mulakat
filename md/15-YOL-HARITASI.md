# 15 — Geliştirme Yol Haritası

## Mevcut Durum (v1.0)

Tamamlanan özellikler:

- Kullanıcı kayıt/giriş (JWT)
- Mülakat oturumu oluşturma ve soru akışı
- Kamera ve mikrofon ile gerçek zamanlı kayıt
- MediaPipe ile görüntü analizi (göz teması, duruş, fidget, jitter)
- Streaming ASR ile transkript toplama
- Oturum finalize ve skor hesaplama
- Puanlama profilleri (genel, teknik, HR)
- LLM coaching (OpenAI-compat format)
- Rapor görüntüleme ve dışa aktarma
- Admin paneli ve batch coaching
- Gizlilik kontrolleri (PII redaction, retention)
- Test altyapısı (unit, integration, performance)

## Öncelikli Yapılacaklar

### P0 — Kritik (Bu Sprint)

#### Claude API Entegrasyonu
Mevcut `OpenAiResponsesClient` yerine `AnthropicClient` yazılacak. Anthropic'in `/v1/messages` formatını kullanacak. Konfigürasyon ile Claude/Ollama arasında geçiş yapılabilecek.

**Dosyalar:**
- `src/backend/InterviewCoach.Api/Services/AnthropicClient.cs` (yeni)
- `src/backend/InterviewCoach.Api/Services/OllamaClient.cs` (mevcut, refactor)
- `src/backend/InterviewCoach.Api/Program.cs` (DI güncelleme)
- `src/backend/InterviewCoach.Api/appsettings.json` (config güncelleme)

#### Canlı Transkript Kaldırma
InterviewSession'dan canlı transkript gösterimini kaldır. Veri toplama devam edecek, sadece UI'dan çıkarılacak.

**Dosyalar:**
- `src/frontend/src/pages/InterviewSession.tsx` (LiveTranscript referansları temizle)
- `src/frontend/src/components/LiveTranscript.tsx` (sil)

#### Rapor Transkript İyileştirme
Raporda transkriptin düzgün gösterilmesi. Soru bazlı transkript segmentleri.

**Dosyalar:**
- `src/frontend/src/pages/Report.tsx` (transkript bölümü güncelle)

### P1 — Yüksek Öncelik (Sonraki Sprint)

#### Yetkinlik Değerlendirmesi Derinleştirme
Claude coaching prompt'una yetkinlik boyutları ekle:
- Teknik yetkinlik puanlaması (soruya özel)
- Davranışsal yetkinlik analizi
- Soru bazlı güçlü/zayıf yön tespiti
- İdeal cevap karşılaştırması (opsiyonel)

#### Dashboard Sayfası
Kullanıcının geçmiş mülakatlarını özetleyen dashboard:
- Toplam mülakat sayısı
- Ortalama skor trendi (çizgi grafik)
- En zayıf alanlar
- Son 5 mülakat özeti

#### Soru Havuzu Genişletme
Admin panelinden yeni sorular eklenebilmesi:
- Kategori bazlı soru yönetimi
- Zorluk seviyesi ataması
- Import/export (JSON formatında)

### P2 — Orta Öncelik

#### Kullanıcı Profili
- CV/Resume yükleme
- Deneyim seviyesi seçimi
- Hedef pozisyon belirleme
- Profil bilgileri coaching prompt'a dahil edilecek

#### İlerleme Takibi (UserProgress)
- Metrik bazlı ilerleme kaydı
- Zaman serisi görselleştirme
- Hedef belirleme ve takip

#### PDF Rapor Dışa Aktarma
- Tarayıcı yazdırma yerine sunucu tarafında PDF üretimi
- Profesyonel şablon ile format
- Grafik ve metrik kartlarının PDF'e dahil edilmesi

### P3 — Düşük Öncelik (Gelecek Sürümler)

#### Çoklu Mülakat Modu
- Teknik mülakat: Kodlama soruları ile
- Case study: Senaryo bazlı sorular
- Panel mülakat: Birden fazla soru soran (simüle)

#### Sesli AI Mülakatçı
- Text-to-Speech ile soruların sesli okunması
- Daha gerçekçi mülakat deneyimi

#### Mobil Optimizasyon
- Responsive tasarım iyileştirmeleri
- PWA desteği
- Kamera/mikrofon mobil uyumluluk

#### Çoklu Dil Genişletme
- Almanca, Fransızca, İspanyolca destek
- Dil bazlı soru havuzları
- Dil bazlı dolgu kelime tespiti

## Rapor ile Proje Arasındaki Fark Analizi

Proje gereksinim raporunda (ister analizi) belirtilip projede henüz bulunmayan veya farklı olan maddeler:

| Rapordaki Madde | Durum | Açıklama |
|----------------|-------|----------|
| Blazor WebAssembly frontend | Farklı | React + Vite kullanılıyor (daha modern) |
| SQL Server LocalDB | Farklı | PostgreSQL kullanılıyor (daha uygun) |
| Metin tabanlı cevap girişi | Farklı | Video/ses kaydı ile (çok daha gelişmiş) |
| İdeal cevap karşılaştırması (FR_008) | Eksik | P1'de planlandı |
| Dashboard (FR_004) | Eksik | P1'de planlandı |
| UserProgress (FR_012) | Eksik | P2'de planlandı |
| CV/Resume profil (FR_003) | Eksik | P2'de planlandı |
| Canlı görüntü/ses analizi | Projede Var | Raporda "kapsam dışı" denmiş ama projede var |
| LLM entegrasyonu | Projede Var | Raporda "sonraki sürüm" denmiş ama projede var |

Proje, rapordan çok daha ileri seviyede. Rapor güncellenmeli.

## Teknik Borç

Mevcut kodda düzeltilmesi gereken teknik borç maddeleri:

- `InterviewSession.tsx` çok büyük (~1400 satır) → Custom hook'lara böl
- `LiveTranscript.tsx` import edilmiş ama kullanılmıyor → Sil
- `OllamaClient.cs` adı yanıltıcı → `OpenAiCompatClient.cs` olarak değiştir
- Bazı CSS sınıf isimleri İngilizce-Türkçe karışık → Standartlaştır
- Frontend'de hata mesajları Türkçe-İngilizce karışık → Dil dosyası (i18n)
- Backend model routing'de `gpt-5.4` referansları var → Güncel model adlarına çevir
