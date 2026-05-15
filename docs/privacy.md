# Gizlilik ve Veri Saklama

## Varsayılan Davranış

Ham ses/video verileri **kalıcı olarak saklanmaz**. Yalnızca türetilmiş metrikler ve transkriptler depolanır.

**Saklanan veriler:**
- Görüntü metrikleri (göz teması oranı, duruş skoru, kıpırdama indeksi, baş titremi)
- Konuşma-yazı dönüşümü (transkript parçaları — varsayılan olarak PII maskelenerek)
- Skor kartları ve LLM koçluk çıktıları

**Saklanmayan veriler (varsayılan):**
- Webcam video dosyaları
- Ham ses kayıtları
- Orijinal (maskelenmemiş) transkript metinleri

---

## Transcript PII Maskeleme

Sistem, transkript metinlerini alırken ve saklarken otomatik olarak kişisel bilgileri maskeler. Bu davranış `Privacy` yapılandırması ile kontrol edilir:

| Ayar | Varsayılan | Açıklama |
|------|-----------|---------|
| `Privacy__RedactTranscripts` | `true` | Transkript metnindeki PII'ları maskele |
| `Privacy__RedactOnIngest` | `true` | Veri alınırken (ingest) anında maskele |
| `Privacy__StoreOriginalTranscripts` | `false` | Maskelenmemiş orijinal metni sakla |

Maskelenen örnekler: TC kimlik numaraları, telefon numaraları, e-posta adresleri, kredi kartı numaraları.

---

## Veri Saklama Politikası (Retention)

Sistem, eski verileri otomatik olarak temizler. Temizleme işlemi her gün UTC 03:00'te çalışır.

| Ayar | Varsayılan | Açıklama |
|------|-----------|---------|
| `Retention__Enabled` | `true` | Otomatik temizlemeyi etkinleştir |
| `Retention__DeleteAfterDays` | `30` | 30 günden eski oturumları tamamen sil |
| `Retention__KeepSummariesOnlyAfterDays` | `7` | 7 günden eski ham metrikleri sil, yalnızca özeti tut |
| `Retention__RunHourUtc` | `3` | Temizlemenin çalışacağı UTC saati |

Bu değerler `docker/.env.production` dosyasından veya ortam değişkenleriyle üzerine yazılabilir.

---

## Ham Medya Saklama (Opsiyonel)

Ham video/ses dosyalarının saklanması, her oturum için `SettingsJson` alanında açıkça etkinleştirilmelidir. Bu seçenek kullanıcı tarafından bilinçli olarak açılmalıdır.

> Şu anda bu özelliği açan bir kullanıcı arayüzü bulunmamaktadır. Yalnızca API üzerinden veya admin panelinden etkinleştirilebilir.

---

## KVKK Notu

Bu sistem Türkiye'de kullanılmak üzere geliştirilmiştir ve 6698 sayılı Kişisel Verilerin Korunması Kanunu (KVKK) kapsamındaki yükümlülüklere tabidir.

Üretim ortamına geçmeden önce değerlendirilmesi gereken konular:
- **Aydınlatma yükümlülüğü:** Kullanıcılar hangi verilerin işlendiği konusunda bilgilendirilmelidir.
- **Açık rıza:** Görüntü ve ses analizi için kullanıcı onayı alınmalıdır.
- **Veri minimizasyonu:** Yalnızca gerekli veriler işlenmeli (varsayılan yapılandırma buna uygundur).
- **Silme hakkı:** Kullanıcıların kendi oturumlarını silebilmesi için bir mekanizma sağlanmalıdır (`DELETE /api/sessions/{id}` mevcut).
- **Veri işleme kaydı:** Hangi verilerin işlendiği, nerede saklandığı ve ne kadar süre tutulduğu kayıt altına alınmalıdır.
