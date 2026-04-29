using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}/[controller]")]
public class QuestionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public QuestionsController(ApplicationDbContext db)
    {
        _db = db;
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
    /// Uploads the audio recording for a specific question (by 1-based order index).
    /// </summary>
    [Authorize]
    [HttpPost("{questionOrder:int}/audio")]
    [RequestSizeLimit(50 * 1024 * 1024)] // 50 MB max
    public async Task<ActionResult<QuestionAudioUploadResponse>> UploadQuestionAudio(
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
            return BadRequest(new { error = "Audio file is empty." });

        // Store the audio file in /app/audio/{sessionId}/ inside the container
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

        // Store the relative URL so the client can fetch via /audio/{sessionId}/q{n}.webm
        var audioUrl = $"/audio/{sessionId}/{fileName}";
        question.AudioUrl = audioUrl;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new QuestionAudioUploadResponse { AudioUrl = audioUrl });
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
