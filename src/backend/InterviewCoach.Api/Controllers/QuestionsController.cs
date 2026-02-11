using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
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

    private QuestionDto ToDto(Question q)
    {
        return new QuestionDto
        {
            Id = q.Id,
            SessionId = q.SessionId,
            Order = q.Order,
            Prompt = q.Prompt,
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
    public DateTime CreatedAt { get; set; }
}
