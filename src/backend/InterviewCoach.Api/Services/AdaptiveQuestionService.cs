using System.Text.Json;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Services;

public interface IAdaptiveQuestionService
{
    Task<List<string>> GenerateAsync(Guid sessionId, CancellationToken ct = default);
}

public sealed class AdaptiveQuestionService : IAdaptiveQuestionService
{
    private readonly ApplicationDbContext _db;
    private readonly ILlmClient _llm;

    private static readonly JsonElement AdaptiveSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "questions": {
              "type": "array",
              "items": { "type": "string" },
              "minItems": 4,
              "maxItems": 4
            }
          },
          "required": ["questions"]
        }
        """).RootElement;

    public AdaptiveQuestionService(ApplicationDbContext db, ILlmClient llm)
    {
        _db = db;
        _llm = llm;
    }

    public async Task<List<string>> GenerateAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.Sessions.FindAsync([sessionId], ct);
        if (session is null) return [];

        var questions = await _db.Questions
            .Where(q => q.SessionId == sessionId && q.Order <= 3)
            .OrderBy(q => q.Order)
            .ToListAsync(ct);

        var segments = await _db.TranscriptSegments
            .Where(s => s.SessionId == sessionId && s.QuestionOrder <= 3)
            .OrderBy(s => s.QuestionOrder)
            .ThenBy(s => s.StartMs)
            .ToListAsync(ct);

        var transcriptByOrder = segments
            .GroupBy(s => s.QuestionOrder ?? 0)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(s => s.Text)));

        var qaBlocks = questions.Select(q =>
        {
            var answer = transcriptByOrder.TryGetValue(q.Order, out var t) && !string.IsNullOrWhiteSpace(t)
                ? t
                : "(no transcript recorded)";
            return $"Q{q.Order}: {q.Prompt}\nAnswer: {answer}";
        });

        var role = session.SelectedRole ?? "Software Engineer";
        var language = (session.Language ?? "tr").ToLower() == "en" ? "English" : "Turkish";

        var qaText = string.Join("\n\n", qaBlocks);
        var systemPrompt = $"You are an expert technical interviewer for {role} positions. " +
                           $"The interview is conducted in {language}. " +
                           "You generate adaptive follow-up questions based on candidate answers. " +
                           "Always respond with valid JSON only — no markdown, no explanation.";

        var userPrompt = $"The candidate was asked 3 questions and provided the following answers:\n\n" +
                         $"{qaText}\n\n" +
                         $"Based on these responses, generate exactly 4 new interview questions that:\n" +
                         $"- Stay strictly within the {role} domain\n" +
                         $"- Are written in {language}\n" +
                         "- Address knowledge gaps or explore strong areas in more depth\n" +
                         "- Vary naturally in difficulty based on demonstrated performance\n\n" +
                         "Return ONLY this JSON structure:\n" +
                         "{\"questions\": [\"question 1\", \"question 2\", \"question 3\", \"question 4\"]}";

        var response = await _llm.GenerateJsonAsync(new LlmJsonRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            SchemaName = "adaptive_questions",
            Schema = AdaptiveSchema
        }, ct);

        return ParseQuestions(response.Content);
    }

    private static List<string> ParseQuestions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            // Strip markdown fences if present
            var trimmed = json.Trim();
            if (trimmed.StartsWith("```"))
            {
                var start = trimmed.IndexOf('\n') + 1;
                var end = trimmed.LastIndexOf("```");
                if (end > start) trimmed = trimmed[start..end].Trim();
            }

            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("questions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Take(4)
                    .ToList();
            }
        }
        catch
        {
            // ignore parse errors
        }
        return [];
    }
}
