# Yapay Zeka Mülakat - Speech-to-Text (STT) Modeli Dokümantasyonu

Bu doküman, sistemin "Konuşmayı Metne Çevirme" (Speech-to-Text) servisi olan `faster-whisper` altyapısını, yüksek kaliteli analiz modunu ve teknik konfigürasyonlarını detaylandırmak için hazırlanmıştır.

## 1. Mimari ve Temel Model

Sistemimizde OpenAI'ın geliştirdiği Whisper mimarisinin C++ tabanlı, yüksek düzeyde optimize edilmiş hali olan **Faster-Whisper** kullanılmaktadır. 

- **Aktif Model:** `large-v3`
- **Neden `large-v3`?** 
  - Mülakat değerlendirmelerinde kelimelerin doğruluğu (Örn: "Java" yerine "çaba" anlaşılmaması) çok kritiktir.
  - Canlı deşifre (live transcription) yerine kayıt sonu analiz (post-processing) mantığına geçildiği için modelin hızı veya gecikme (latency) süresi önemini kaybetmiş, kalite 1 numaralı öncelik haline gelmiştir.
  - Özellikle Türkçe'deki devrik cümleler, aksanlar ve teknik terimlerin doğru deşifresi için `large-v3` modeli açık ara en iyi performansı göstermektedir.
  
## 2. Model Konfigürasyonu ve Beam Search (Arama Stratejisi)

Deşifre kalitesini artırmak için Beam Search (Işın Arama) algoritmik derinliği artırılmıştır:

- **`SPEECH_BEAM_SIZE = 5`**: Model, bir kelimeyi tahmin ederken anlık olarak en olası 5 farklı cümleyi eşzamanlı olarak dallandırır (branching) ve ilerler. Varsayılan (2) yerine 5 kullanılması, kelime hatası (WER - Word Error Rate) oranını ciddi ölçüde düşürür.
- **`SPEECH_BEST_OF = 5`**: Modelin oluşturduğu tahmin dalları arasından sıcaklık (temperature) fallback mekanizmalarını kullanarak "en iyi" olanı seçmesini sağlar.
- **`SPEECH_NO_SPEECH_THRESHOLD = 0.6`**: Boşluk veya arka plan seslerini halüsinasyon (olmayan kelimeleri üretme) olarak algılamaması için belirlenmiş güvenlik bariyeridir.

## 3. Optimizasyon ve Donanım Gereksinimleri

`large-v3` modeli büyük bir model olmasına rağmen, Faster-Whisper'ın `CTranslate2` altyapısı kullanılarak oldukça optimize çalışır.

- **`SPEECH_COMPUTE_TYPE = int8_float16`**: Modelin ağırlıkları `int8` (8-bit tam sayı) değerlerine quantize (sıkıştırılmış) edilir, hesaplamalar ise `float16` (16-bit ondalıklı sayı) formatında GPU üzerinde yapılır. Bu strateji kaliteden görünür bir taviz vermezken, ekran kartı (VRAM) belleğinden ciddi oranda tasarruf sağlar.
- **`SPEECH_DEVICE = cuda`**: Bütün tensör matematik işlemleri NVIDIA GPU (CUDA) çekirdeklerinde hesaplanır. 

## 4. Voice Activity Detection (VAD)

Konuşma olmayan kısımların (sessizlik, klavye sesi, nefes alma vb.) modele gönderilmeden önce filtrelenmesi için donanımsal bir VAD (Ses Aktivitesi Algılayıcısı) kullanıyoruz.

- **VAD Backend:** `silero` (veya desteklenmeyen donanımlarda `energy` fallback).
- Silero VAD, insan sesine duyarlı yapay sinir ağı tabanlı oldukça verimli bir dedektördür.
- **`VAD_SILENCE_MS = 900`**: Kullanıcının konuşma sırasındaki doğal duraksamalarını (es verme) kelime veya cümle sonu gibi algılamaması için 900 milisaniye beklenir. Kısa nefes almalar veya düşünme anları cümleyi bölmez.
- **`VAD_MIN_SPEECH_MS = 300`**: Kazara çıkan çok kısa seslerin (öksürük, yutkunma, sandalye gıcırtısı vb.) modele gereksiz yere "konuşma" olarak gitmesini engeller.

## 5. Uygulama İçi (Frontend) Akışının Değişimi

Mülakat sırasında canlı deşifre (Live Transcript) özelliği devreden çıkartıldığı için uygulamanın konuşmayı işleme davranışı şu şekildedir:

1. **Toplama Aşaması**: Aday soruyu yanıtlarken mikrofon ve kamera eşzamanlı olarak tarayıcı üzerinde kaydedilir (`video/webm` veya desteklenen formatta).
2. **Gönderme Aşaması**: Aday yanıtını bitirip "Next Question" (Sonraki Soru) butonuna bastığı anda bu medya paketi asenkron olarak arka plana (Backend) yüklenir.
3. **Deşifre İşlemi**: Backend üzerinde yer alan API, bu videonun/sesin içindeki konuşmayı ayrıştırır ve `large-v3` modeline aktarır. (Mülakat devam ederken önceki sorular arka planda deşifre edilmeye başlar).
4. **Analiz ve Sonuç**: Tüm sorular bittiğinde Rapor ekranı (`Report.tsx`) açılır. Bu ekranda adayın her bir soru için yaptığı tam kayıt izlenebilir. Ayrıca yüksek kaliteli tam mülakat deşifresi ("Görüşme Kayıtları ve Deşifre" paneli) tek bir bütün halinde sunulur. Yüksek kaliteli bu metin, asıl LLM değerlendirme modülü tarafından okunarak en doğru puanlamayı sağlar.
