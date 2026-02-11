namespace InterviewAI.Services;

public interface IInterviewAiService
{
    Task<AiQuestionResult?> GenerateNextQuestionAsync(AiQuestionRequest request, CancellationToken cancellationToken = default);
    Task<AiEvaluationResult?> EvaluateAnswerAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default);
}

public record AiQuestionRequest(
    string Role,
    string Difficulty,
    int QuestionNumber,
    int TotalQuestions,
    IReadOnlyList<string> PreviousQuestions);

public record AiQuestionResult(
    string Question,
    string FollowUpHint);

public record AiEvaluationRequest(
    string Question,
    string Answer,
    string Role,
    string Difficulty,
    double EyeContactScore,
    double PostureScore,
    double ConfidenceScore);

public record AiEvaluationResult(
    double Score,
    string CoachingTip);
