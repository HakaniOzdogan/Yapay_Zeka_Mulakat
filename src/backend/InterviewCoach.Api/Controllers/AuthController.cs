using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthController(
        ApplicationDbContext db,
        IPasswordHasher<User> passwordHasher,
        IJwtTokenService jwtTokenService)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["email"] = ["email is required."]
                });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["password"] = ["password must be at least 8 characters."]
                });
        }

        var exists = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.EmailNormalized == email);

        if (exists)
        {
            return this.Problem(
                StatusCodes.Status409Conflict,
                "Conflict",
                "An account with this email already exists.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            EmailNormalized = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
            Role = UserRoles.User
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        var token = _jwtTokenService.CreateAccessToken(user);

        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = token
        });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return this.Problem(
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Invalid email or password.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == email);
        if (user == null || !user.IsActive)
        {
            return this.Problem(
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Invalid email or password.");
        }

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return this.Problem(
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Invalid email or password.");
        }

        var token = _jwtTokenService.CreateAccessToken(user);

        return Ok(new AuthResponse
        {
            UserId = user.Id,
            Email = user.Email,
            Token = token
        });
    }

    private static string NormalizeEmail(string? email)
    {
        return (email ?? string.Empty).Trim().ToLowerInvariant();
    }
}

public class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class AuthResponse
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}
