# ASR Benchmark Dataset

Bu klasor benchmark-first ASR iyilestirme akisi icin veri seti sablonlarini tutar.

## Hedef

- `tiny`, `small`, gerekirse `medium` modellerini ayni veri setiyle olcmek
- Turkce konusma icinde gecen Ingilizce teknik terimlerin ne kadar korundugunu gormek
- Ilk partial, ilk final ve genel transcript kalitesini yan yana karsilastirmak

## Kaynak Turleri

- `real_mic`
  - Kendi bilgisayarindaki gercek mikrofon kayitlari
  - Oda sesi, mikrofon kalitesi ve konusma aliskanliklarini gercekci yansitir
- `reference_read`
  - Kontrollu ve temiz okunmus ornekler
  - Modelleri daha stabil sartlarda karsilastirmayi kolaylastirir

## Onerilen Klasor Yapisi

```text
services/speech-service/perf/datasets/
  README.md
  asr_benchmark_manifest.example.json
  audio/
    real_mic/
    reference_read/
```

## Ses Dosyasi Notlari

- Benchmark scripti PCM16 mono 16kHz WAV dosyalarini dogrudan kullanir.
- Farkli bir format verirseniz script `ffmpeg` bulursa otomatik donusturur.
- `ffmpeg` yoksa benchmark oncesi dosyalari WAV 16k mono hale getirmeniz gerekir.

## Manifest Kullanimi

- `audio_path`: ses dosyasinin repo kokune gore goreli veya tam yolu
- `reference_text`: beklenen dogru transcript
- `expected_terms`: transcript icinde korunmasini ozellikle takip edecegin teknik terimler
- `notes`: istege bagli serbest not

## Onerilen Is Akisi

1. `asr_benchmark_manifest.example.json` dosyasini kopyalayip kendi manifest dosyanizi olusturun.
2. Ses dosyalarini `audio/real_mic` ve `audio/reference_read` altina koyun.
3. Her sample icin `reference_text` ve `expected_terms` alanlarini doldurun.
4. Benchmark'i once `tiny`, sonra `small`, gerekirse `medium` ile calistirin.

## Ornek Komutlar

```bash
python services/speech-service/perf/run_asr_dataset_benchmark.py \
  --manifest services/speech-service/perf/datasets/asr_benchmark_manifest.example.json \
  --models tiny,small \
  --base-url http://localhost:8000
```

```bash
python services/speech-service/perf/run_asr_dataset_benchmark.py \
  --manifest services/speech-service/perf/datasets/asr_benchmark_manifest.example.json \
  --models small \
  --sample-id tr-tech-mixed-short-01 \
  --base-url http://localhost:8000
```
