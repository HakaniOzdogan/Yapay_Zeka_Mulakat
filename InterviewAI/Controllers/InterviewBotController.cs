using InterviewAI.Data;
using InterviewAI.Models;
using InterviewAI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewAI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InterviewBotController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IInterviewAiService _aiService;

    private static readonly Dictionary<string, List<string>> QuestionBank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Technical"] =
        [
            "Dependency injection nedir ve bir projede neden kritik olur?",
            "Asenkron programlama kullanirken deadlock riskini nasil azaltirsin?",
            "Bir API'de performans darboqazini nasil tespit eder ve cozersin?",
            "SQL sorgularinda index kullanimini neye gore planlarsin?",
            "Clean Architecture yaklasimini bir ornek uzerinden aciklar misin?"
        ],
        ["Behavioral"] =
        [
            "Zor bir takim catismasini nasil yonettigini anlatir misin?",
            "Kisa surede yetismesi gereken bir isi nasil planlarsin?",
            "Olumsuz geri bildirim aldiginda ilk aksiyonun ne olur?",
            "Belirsiz gereksinimleri nasil netlestirirsin?",
            "Oncelik cakismalarinda kararini nasil verirsin?"
        ],
        ["HR"] =
        [
            "Bu rol icin seni motive eden temel neden nedir?",
            "5 yil sonra kariyerini nerede goruyorsun?",
            "Guclu ve gelistirmen gereken yonlerini nasil tanimlarsin?",
            "Yogun baski altinda calisma tarzini anlatir misin?",
            "Neden bu sirketi tercih ettigini anlatir misin?"
        ],
        ["General"] =
        [
            "Kendini bu pozisyon icin neden uygun goruyorsun?",
            "Bir problemle karsilastiginda yaklasimin nasildir?",
            "Ogrenme hizini artirmak icin hangi yontemleri kullanirsin?",
            "Bir projede kaliteyi nasil garanti altina alirsin?",
            "Iletisimde en cok dikkat ettigin nokta nedir?"
        ]
    };

    public InterviewBotController(ApplicationDbContext context, IInterviewAiService aiService)
    {
        _context = context;
        _aiService = aiService;
    }

    [HttpPost("session/{interviewId:int}/next")]
    public async Task<ActionResult<BotTurnResponseDto>> NextTurn(int interviewId, [FromBody] BotTurnRequestDto request)
    {
        var interview = await _context.Interviews
            .Include(i => i.Questions.OrderBy(q => q.OrderNumber))
            .FirstOrDefaultAsync(i => i.Id == interviewId);

        if (interview == null)
        {
            return NotFound($"Interview {interviewId} not found.");
        }

        if (interview.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(BuildCompletedResponse(interview, request));
        }

        var lastQuestion = interview.Questions
            .OrderByDescending(q => q.OrderNumber)
            .FirstOrDefault();

        var latestCoachingTip = BuildHeuristicCoachingTip(request.Mediapipe);
        if (lastQuestion != null && string.IsNullOrWhiteSpace(lastQuestion.UserAnswer) && !string.IsNullOrWhiteSpace(request.UserAnswer))
        {
            var aiEval = await _aiService.EvaluateAnswerAsync(new AiEvaluationRequest(
                lastQuestion.Question,
                request.UserAnswer.Trim(),
                string.IsNullOrWhiteSpace(interview.JobPosition) ? "General" : interview.JobPosition,
                DifficultyText(interview.DifficultyLevel),
                request.Mediapipe?.EyeContactScore ?? 60,
                request.Mediapipe?.PostureScore ?? 60,
                request.Mediapipe?.ConfidenceScore ?? 60));

            var answerScore = aiEval?.Score ?? EvaluateAnswerScore(request.UserAnswer, request.Mediapipe);
            latestCoachingTip = string.IsNullOrWhiteSpace(aiEval?.CoachingTip)
                ? latestCoachingTip
                : aiEval!.CoachingTip;

            lastQuestion.UserAnswer = request.UserAnswer.Trim();
            lastQuestion.AnsweredAt = DateTime.UtcNow;
            lastQuestion.Score = answerScore;
            lastQuestion.TimeSpentSeconds = request.AnswerDurationSeconds ?? 0;

            if (answerScore >= 60)
            {
                interview.CorrectAnswers += 1;
            }
        }

        var answeredCount = interview.Questions.Count(q => !string.IsNullOrWhiteSpace(q.UserAnswer));
        if (answeredCount >= interview.TotalQuestions)
        {
            FinalizeInterview(interview, request.Mediapipe);
            await UpsertCompletionFeedbackAsync(interview, latestCoachingTip);
            await _context.SaveChangesAsync();
            return Ok(BuildCompletedResponse(interview, request, latestCoachingTip));
        }

        var nextOrder = interview.Questions.Count + 1;
        var previousQuestions = interview.Questions.OrderBy(q => q.OrderNumber).Select(q => q.Question).ToList();
        var role = string.IsNullOrWhiteSpace(interview.JobPosition) ? "General" : interview.JobPosition;
        var questionType = role;

        var aiQuestion = await _aiService.GenerateNextQuestionAsync(new AiQuestionRequest(
            role,
            DifficultyText(interview.DifficultyLevel),
            nextOrder,
            interview.TotalQuestions,
            previousQuestions));

        var questionText = string.IsNullOrWhiteSpace(aiQuestion?.Question)
            ? GenerateQuestion(interview.JobPosition, nextOrder)
            : aiQuestion.Question.Trim();

        var followUpHint = string.IsNullOrWhiteSpace(aiQuestion?.FollowUpHint)
            ? "Yanitinda ornek, metrik ve sonuc bilgisi vermeye calis."
            : aiQuestion.FollowUpHint.Trim();

        interview.Questions.Add(new InterviewQuestion
        {
            InterviewId = interview.Id,
            Question = questionText,
            QuestionType = questionType,
            OrderNumber = nextOrder,
            Difficulty = DifficultyText(interview.DifficultyLevel)
        });

        await _context.SaveChangesAsync();

        return Ok(new BotTurnResponseDto
        {
            IsCompleted = false,
            InterviewId = interview.Id,
            QuestionNumber = nextOrder,
            Question = questionText,
            CoachingTip = latestCoachingTip,
            SuggestedFollowUp = followUpHint
        });
    }

    private static BotTurnResponseDto BuildCompletedResponse(Interview interview, BotTurnRequestDto request, string? coachingTip = null)
    {
        return new BotTurnResponseDto
        {
            IsCompleted = true,
            InterviewId = interview.Id,
            QuestionNumber = interview.TotalQuestions,
            OverallScore = interview.OverallScore,
            CoachingTip = string.IsNullOrWhiteSpace(coachingTip) ? BuildHeuristicCoachingTip(request.Mediapipe) : coachingTip,
            SuggestedFollowUp = "Mulakat tamamlandi. Dashboard ekranindan sonucu inceleyebilirsin."
        };
    }

    private void FinalizeInterview(Interview interview, MediapipeSignalsDto? mediapipe)
    {
        var answerScore = interview.Questions.Any()
            ? interview.Questions.Average(q => q.Score)
            : 0;

        var eyeContact = mediapipe?.EyeContactScore ?? 60;
        var posture = mediapipe?.PostureScore ?? 60;
        var confidence = mediapipe?.ConfidenceScore ?? 60;

        interview.OverallScore = Math.Round((answerScore * 0.7) + (eyeContact * 0.1) + (posture * 0.1) + (confidence * 0.1), 2);
        interview.CompletedAt = DateTime.UtcNow;
        interview.Status = "Completed";
    }

    private async Task UpsertCompletionFeedbackAsync(Interview interview, string coachingTip)
    {
        var existing = await _context.Feedbacks
            .Where(f => f.InterviewId == interview.Id && f.Category == "BotSummary")
            .ToListAsync();

        if (existing.Count > 0)
        {
            _context.Feedbacks.RemoveRange(existing);
        }

        _context.Feedbacks.Add(new Feedback
        {
            InterviewId = interview.Id,
            Category = "BotSummary",
            SeverityLevel = interview.OverallScore >= 75 ? 1 : 2,
            Comment = $"Toplam skor: {interview.OverallScore:0.##}. Cevap kalitesi ve iletisim birlikte degerlendirildi.",
            Recommendation = coachingTip
        });
    }

    private static double EvaluateAnswerScore(string answer, MediapipeSignalsDto? mediapipe)
    {
        var lengthScore = Math.Clamp(answer.Length / 4.0, 20, 75);
        var confidenceBoost = (mediapipe?.ConfidenceScore ?? 50) * 0.2;
        var eyeContactBoost = (mediapipe?.EyeContactScore ?? 50) * 0.1;
        var postureBoost = (mediapipe?.PostureScore ?? 50) * 0.1;
        return Math.Round(Math.Clamp(lengthScore + confidenceBoost + eyeContactBoost + postureBoost, 0, 100), 2);
    }

    private static string GenerateQuestion(string? jobPosition, int order)
    {
        var key = string.IsNullOrWhiteSpace(jobPosition) ? "General" : jobPosition.Trim();
        if (!QuestionBank.TryGetValue(key, out var questions))
        {
            questions = QuestionBank["General"];
        }

        return questions[(order - 1) % questions.Count];
    }

    private static string DifficultyText(int level) => level switch
    {
        <= 1 => "Easy",
        2 => "Medium",
        _ => "Hard"
    };

    private static string BuildHeuristicCoachingTip(MediapipeSignalsDto? mediapipe)
    {
        if (mediapipe == null)
        {
            return "Yanitlarini STAR yapisinda kur: durum, aksiyon, sonuc.";
        }

        var tips = new List<string>();
        if (mediapipe.EyeContactScore < 55) tips.Add("Kameraya daha sik bakarak goz temasini artir.");
        if (mediapipe.PostureScore < 55) tips.Add("Omuzlarini dengede tutup daha dik otur.");
        if (mediapipe.ConfidenceScore < 55) tips.Add("Daha net ifade ve somut ornek kullan.");

        return tips.Count == 0
            ? "Iyi gidiyorsun. Kisa, net ve ornekli cevaplar vermeye devam et."
            : string.Join(" ", tips);
    }
}

public class BotTurnRequestDto
{
    public string? UserAnswer { get; set; }
    public double? AnswerDurationSeconds { get; set; }
    public MediapipeSignalsDto? Mediapipe { get; set; }
}

public class MediapipeSignalsDto
{
    public double EyeContactScore { get; set; }
    public double PostureScore { get; set; }
    public double ConfidenceScore { get; set; }
    public double AttentionScore { get; set; }
}

public class BotTurnResponseDto
{
    public bool IsCompleted { get; set; }
    public int InterviewId { get; set; }
    public int QuestionNumber { get; set; }
    public string? Question { get; set; }
    public string? CoachingTip { get; set; }
    public string? SuggestedFollowUp { get; set; }
    public double? OverallScore { get; set; }
}
