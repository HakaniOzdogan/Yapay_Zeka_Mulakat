using System.Net.Http.Json;

namespace InterviewAI.Frontend.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiBaseUrl = "https://localhost:7182/api";

        public ApiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        // Users
        public async Task<List<UserDto>> GetUsersAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<UserDto>>($"{_apiBaseUrl}/users") ?? new();
        }

        public async Task<UserDto> GetUserAsync(int id)
        {
            return await _httpClient.GetFromJsonAsync<UserDto>($"{_apiBaseUrl}/users/{id}");
        }

        public async Task<UserDto> CreateUserAsync(UserDto user)
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiBaseUrl}/users", user);
            return await response.Content.ReadAsJsonAsync<UserDto>();
        }

        public async Task UpdateUserAsync(int id, UserDto user)
        {
            await _httpClient.PutAsJsonAsync($"{_apiBaseUrl}/users/{id}", user);
        }

        public async Task DeleteUserAsync(int id)
        {
            await _httpClient.DeleteAsync($"{_apiBaseUrl}/users/{id}");
        }

        // Interviews
        public async Task<List<InterviewDto>> GetInterviewsAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<InterviewDto>>($"{_apiBaseUrl}/interviews") ?? new();
        }

        public async Task<InterviewDto> GetInterviewAsync(int id)
        {
            return await _httpClient.GetFromJsonAsync<InterviewDto>($"{_apiBaseUrl}/interviews/{id}");
        }

        public async Task<List<InterviewDto>> GetUserInterviewsAsync(int userId)
        {
            return await _httpClient.GetFromJsonAsync<List<InterviewDto>>($"{_apiBaseUrl}/interviews/user/{userId}") ?? new();
        }

        public async Task<InterviewDto> CreateInterviewAsync(InterviewDto interview)
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiBaseUrl}/interviews", interview);
            return await response.Content.ReadAsJsonAsync<InterviewDto>();
        }

        public async Task UpdateInterviewAsync(InterviewDto interview)
        {
            await _httpClient.PutAsJsonAsync($"{_apiBaseUrl}/interviews/{interview.Id}", interview);
        }

        public async Task DeleteInterviewAsync(int id)
        {
            await _httpClient.DeleteAsync($"{_apiBaseUrl}/interviews/{id}");
        }

        // Feedbacks
        public async Task<List<FeedbackDto>> GetFeedbacksAsync()
        {
            return await _httpClient.GetFromJsonAsync<List<FeedbackDto>>($"{_apiBaseUrl}/feedback") ?? new();
        }

        public async Task<List<FeedbackDto>> GetInterviewFeedbacksAsync(int interviewId)
        {
            return await _httpClient.GetFromJsonAsync<List<FeedbackDto>>($"{_apiBaseUrl}/feedback/interview/{interviewId}") ?? new();
        }

        public async Task<FeedbackDto> CreateFeedbackAsync(FeedbackDto feedback)
        {
            var response = await _httpClient.PostAsJsonAsync($"{_apiBaseUrl}/feedback", feedback);
            return await response.Content.ReadAsJsonAsync<FeedbackDto>();
        }
    }

    // DTOs
    public class UserDto
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string FullName { get; set; }
        public string Profession { get; set; }
        public int ExperienceLevel { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class InterviewDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string JobPosition { get; set; }
        public string JobField { get; set; }
        public int DifficultyLevel { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; }
        public int TotalQuestions { get; set; }
        public int CorrectAnswers { get; set; }
        public double Duration { get; set; }
        public double OverallScore { get; set; }
    }

    public class FeedbackDto
    {
        public int Id { get; set; }
        public int InterviewId { get; set; }
        public string Category { get; set; }
        public string Comment { get; set; }
        public int SeverityLevel { get; set; }
        public string Recommendation { get; set; }
    }
}
