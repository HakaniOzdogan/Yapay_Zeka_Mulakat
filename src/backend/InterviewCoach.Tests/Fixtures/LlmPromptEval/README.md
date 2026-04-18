# LLM Prompt A/B Evaluation Dataset

Bu klasor LLM coaching prompt varyantlari icin offline/controlled benchmark fixture'larini icerir.

## Gereksinimler

- Ollama calisiyor olmali (`http://localhost:11434` varsayilan)
- Model yuklu olmali (varsayilan: `qwen2.5:7b-instruct`)
- Test etkinlestirme:
  - `RUN_LLM_PROMPT_EVAL=true`

## Ortam Degiskenleri

- `RUN_LLM_PROMPT_EVAL=true` (zorunlu, degilse test skip)
- `LLM_EVAL_BASE_URL` (opsiyonel, varsayilan `http://localhost:11434`)
- `LLM_EVAL_MODEL` (opsiyonel, varsayilan `qwen2.5:7b-instruct`)
- `LLM_EVAL_TIMEOUT_SECONDS` (opsiyonel, varsayilan `60`)

## Calistirma

```bash
RUN_LLM_PROMPT_EVAL=true dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj --filter "FullyQualifiedName~LlmPromptAbEvaluation"
```

PowerShell:

```powershell
$env:RUN_LLM_PROMPT_EVAL="true"
dotnet test src/backend/InterviewCoach.Tests/InterviewCoach.Tests.csproj --filter "FullyQualifiedName~LlmPromptAbEvaluation"
```

## Cikti

- JSON ozet rapor:
  - `artifacts/eval/llm-prompt-ab-<timestamp>.json`

## Heuristic Skor Yorumu

Skorlar ground-truth degildir; prompt kalitesi icin goreli sinyal saglar:

- `schemaValid`: strict JSON schema uyumu
- `feedbackCountScore`: 5-10 feedback hedefine yakinlik
- `evidenceGroundingScore`: evidence dolulugu, time range formati, kategori gecerliligi
- `actionabilityScore`: suggestion/example alanlarinin kalitesi
- `diversityScore`: kategori dagilimi + duplicate baslik cezasi
- `drillQualityScore`: drill sayisi/icerik tutarliligi
- `safetyFormatScore`: markdown fence/extra key cezasi

## Yeni Case Ekleme

1. Yeni evidence JSON ekle (`<caseId>.evidence.json`)
2. `manifest.json` icine case ekle:
   - `caseId`
   - `evidenceFile`
   - `expectedFocus` (opsiyonel ama onerilir)
