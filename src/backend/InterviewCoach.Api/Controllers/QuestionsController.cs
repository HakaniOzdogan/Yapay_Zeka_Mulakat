using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using InterviewCoach.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}/[controller]")]
public class QuestionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IAdaptiveQuestionService _adaptive;

    public QuestionsController(ApplicationDbContext db, IAdaptiveQuestionService adaptive)
    {
        _db = db;
        _adaptive = adaptive;
    }

    // Default questions by role (MVP Turkish)
    private static Dictionary<string, string[]> DefaultQuestions = new()
    {
        ["Software Engineer"] = new[]
        {
            "Bize yakın zamanda başarıyla tamamladığınız bir proje hakkında anlatır mısınız?",
            "Teknik bir zorlukla karşılaştığınızda nasıl bir süreç izlersiniz?",
            "Takımda çalışırken karşılaştığınız en zor durumu nasıl yönetdiniz?",
            "Yeni bir teknoji öğrenirken izlediğiniz adımları açıklayabilir misiniz?"
        },
        ["Product Manager"] = new[]
        {
            "En başarılı ürün özelliğini nasıl tanımladınız?",
            "Kullanıcı araştırması nasıl gerçekleştirirsiniz?",
            "Ürün stratejisi belirlerken hangi metrikleri kullanırsınız?",
            "Başarısızlıktan nasıl öğrendiniz?"
        },
        ["Data Scientist"] = new[]
        {
            "Son yaptığınız en harika veri analizi projesi nedir?",
            "Model seçerken hangi faktörleri göz önünde bulundurursunuz?",
            "Veri kalitesi sorunlarıyla nasıl mücadele edersiniz?",
            "Sonuçları stakeholderlere nasıl sunarsınız?"
        },
        ["UX Designer"] = new[]
        {
            "Tasarımınızı neden bu şekilde seçtiniz?",
            "Kullanıcı testlerinden ne öğrendiniz?",
            "Erişilebilirlik tasarımında dikkat ettiğiniz noktalar nelerdir?",
            "Başarısız bir tasarım üzerinde deneyiminiz var mı?"
        }
    };

    [HttpPost]
    public async Task<ActionResult<List<QuestionDto>>> SeedQuestions(Guid sessionId)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session == null)
            return NotFound();

        // Check if questions already exist
        var existingQuestions = await _db.Questions
            .Where(q => q.SessionId == sessionId)
            .ToListAsync();
        
        if (existingQuestions.Count > 0)
            return Ok(existingQuestions.Select(ToDto).ToList());

        // Get default questions for role
        var prompts = DefaultQuestions.GetValueOrDefault(session.SelectedRole, DefaultQuestions["Software Engineer"]);

        var questions = new List<Question>();
        for (int i = 0; i < prompts.Length; i++)
        {
            var q = new Question
            {
                SessionId = sessionId,
                Order = i + 1,
                Prompt = prompts[i]
            };
            questions.Add(q);
        }

        _db.Questions.AddRange(questions);
        await _db.SaveChangesAsync();

        return Ok(questions.Select(ToDto).ToList());
    }

    [HttpGet]
    public async Task<ActionResult<List<QuestionDto>>> GetQuestions(Guid sessionId)
    {
        var questions = await _db.Questions
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.Order)
            .ToListAsync();

        return Ok(questions.Select(ToDto).ToList());
    }

    /// <summary>
    /// Generates 4 adaptive questions (orders 5–8) based on answers to Q1–Q3.
    /// Idempotent: returns existing adaptive questions if already generated.
    /// </summary>
    [Authorize]
    [HttpPost("adaptive")]
    public async Task<ActionResult<List<QuestionDto>>> GenerateAdaptiveQuestions(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await _db.Sessions.FindAsync([sessionId], cancellationToken);
        if (session == null)
            return NotFound();

        // Idempotent: return existing adaptive questions if already generated
        var existing = await _db.Questions
            .Where(q => q.SessionId == sessionId && q.Order >= 5)
            .OrderBy(q => q.Order)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
            return Ok(existing.Select(ToDto).ToList());

        var prompts = await _adaptive.GenerateAsync(sessionId, cancellationToken);
        if (prompts.Count == 0)
            return StatusCode(502, new { error = "Adaptive question generation returned no results." });

        var startOrder = 5;
        var newQuestions = prompts.Select((prompt, i) => new Question
        {
            SessionId = sessionId,
            Order = startOrder + i,
            Prompt = prompt
        }).ToList();

        _db.Questions.AddRange(newQuestions);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(newQuestions.Select(ToDto).ToList());
    }

    /// <summary>
    /// Uploads the webcam recording for a specific question (by 1-based order index).
    /// Optionally accepts startMs and endMs (milliseconds from session start) for per-question metric windowing.
    /// </summary>
    [Authorize]
    [HttpPost("{questionOrder:int}/audio")]
    [RequestSizeLimit(200 * 1024 * 1024)] // 200 MB max
    public async Task<ActionResult<QuestionAudioUploadResponse>> UploadQuestionAudio(
        Guid sessionId,
        int questionOrder,
        IFormFile file,
        [FromForm] long? startMs,
        [FromForm] long? endMs,
        CancellationToken cancellationToken)
    {
        var question = await _db.Questions
            .FirstOrDefaultAsync(q => q.SessionId == sessionId && q.Order == questionOrder, cancellationToken);

        if (question == null)
            return NotFound(new { error = $"Question with order {questionOrder} not found for session {sessionId}." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Audio file is empty." });

        var audioDir = Path.Combine("audio", sessionId.ToString());
        Directory.CreateDirectory(audioDir);

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".webm";
        var fileName = $"q{questionOrder}{ext}";
        var filePath = Path.Combine(audioDir, fileName);

        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var audioUrl = $"/audio/{sessionId}/{fileName}";
        question.AudioUrl = audioUrl;
        if (startMs.HasValue) question.StartMs = startMs;
        if (endMs.HasValue) question.EndMs = endMs;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new QuestionAudioUploadResponse { AudioUrl = audioUrl });
    }

    /// <summary>
    /// Uploads the screen recording for a specific question.
    /// </summary>
    [Authorize]
    [HttpPost("{questionOrder:int}/screen")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500 MB max
    public async Task<ActionResult<QuestionAudioUploadResponse>> UploadQuestionScreen(
        Guid sessionId,
        int questionOrder,
        IFormFile file,
        CancellationToken cancellationToken)
    {
        var question = await _db.Questions
            .FirstOrDefaultAsync(q => q.SessionId == sessionId && q.Order == questionOrder, cancellationToken);

        if (question == null)
            return NotFound(new { error = $"Question with order {questionOrder} not found for session {sessionId}." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "Screen recording file is empty." });

        var audioDir = Path.Combine("audio", sessionId.ToString());
        Directory.CreateDirectory(audioDir);

        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".webm";
        var fileName = $"q{questionOrder}_screen{ext}";
        var filePath = Path.Combine(audioDir, fileName);

        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var screenUrl = $"/audio/{sessionId}/{fileName}";
        question.ScreenAudioUrl = screenUrl;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new QuestionAudioUploadResponse { AudioUrl = screenUrl });
    }

    private QuestionDto ToDto(Question q)
    {
        return new QuestionDto
        {
            Id = q.Id,
            SessionId = q.SessionId,
            Order = q.Order,
            Prompt = q.Prompt,
            AudioUrl = q.AudioUrl,
            CreatedAt = q.CreatedAt
        };
    }
}

public class QuestionDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int Order { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string? AudioUrl { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class QuestionAudioUploadResponse
{
    public string AudioUrl { get; set; } = string.Empty;
}
