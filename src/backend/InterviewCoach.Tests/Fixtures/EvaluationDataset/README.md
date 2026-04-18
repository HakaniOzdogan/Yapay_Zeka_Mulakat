# Evaluation Dataset

Bu klasor offline skor/pattern kalite degerlendirmesi icin replay tabanli case dosyalarini icerir.

## Dosya Yapisi

- `dataset.manifest.json`: tum case listesini tutar
- `<caseId>.replay.json`: replay import endpoint formatinda session verisi
- `<caseId>.label.json`: beklenen skor/pattern/evidence etiketleri

## Yeni Case Ekleme

1. Yeni replay dosyasi ekle:
   - `my_case.replay.json`
2. Label dosyasi ekle:
   - `my_case.label.json`
3. `dataset.manifest.json` icine case kaydi ekle:
   - `caseId`
   - `replayFile`
   - `labelFile`

## Label Formati

Ornek:

```json
{
  "caseId": "my_case",
  "profile": "technical",
  "expected": {
    "scoreCard": {
      "overall": { "min": 60, "max": 85 },
      "eyeContact": { "min": 50, "max": 100 }
    },
    "patterns": {
      "mustContain": ["Fidget"],
      "mustNotContain": ["EyeContact"]
    },
    "evidence": {
      "minWorstWindows": 1
    }
  }
}
```

## Calistirma

```bash
dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj --filter "FullyQualifiedName~EvaluationDataset"
```

Docker/Testcontainers yoksa integration testler skip olur.

## Sonuclari Yorumlama

- `score range` disina cikma: threshold/weight drift veya scoring bug belirtisi olabilir.
- `mustContain` miss: beklenen pattern tetiklenmiyor (precision/recall sorunu).
- `mustNotContain` hit: istenmeyen pattern tetikleniyor (false positive riski).
- Ozet JSON:
  - `artifacts/eval/evaluation-summary-<timestamp>.json`
