using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "AdminOnly")]
public class AdminUsersController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public AdminUsersController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<AdminUserSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<AdminUserSummaryDto>>> List([FromQuery] int take = 100, CancellationToken cancellationToken = default)
    {
        var safeTake = Math.Clamp(take, 1, 100);

        var users = await _db.Users
            .AsNoTracking()
            .OrderByDescending(u => u.CreatedAtUtc)
            .Take(safeTake)
            .Select(u => new AdminUserSummaryDto
            {
                UserId = u.Id,
                Email = u.Email,
                Role = u.Role,
                CreatedAtUtc = u.CreatedAtUtc,
                IsActive = u.IsActive
            })
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    [HttpPost("{userId:guid}/role")]
    [ProducesResponseType(typeof(AdminUserRoleUpdateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminUserRoleUpdateResponseDto>> UpdateRole(
        Guid userId,
        [FromBody] UpdateUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        var requestedRole = (request.Role ?? string.Empty).Trim();
        if (!UserRoles.IsValid(requestedRole))
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["role"] = [$"role must be one of: {UserRoles.User}, {UserRoles.Admin}."]
                });
        }

        requestedRole = NormalizeRole(requestedRole);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null)
        {
            return this.NotFoundProblem($"User '{userId}' was not found.");
        }

        if (user.Role == requestedRole)
        {
            return Ok(new AdminUserRoleUpdateResponseDto
            {
                UserId = user.Id,
                Email = user.Email,
                Role = user.Role
            });
        }

        if (user.Role == UserRoles.Admin && requestedRole == UserRoles.User)
        {
            var adminCount = await _db.Users.CountAsync(
                u => u.IsActive && u.Role == UserRoles.Admin,
                cancellationToken);

            if (adminCount <= 1)
            {
                return this.ConflictProblem("Cannot demote the last remaining admin.");
            }
        }

        user.Role = requestedRole;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new AdminUserRoleUpdateResponseDto
        {
            UserId = user.Id,
            Email = user.Email,
            Role = user.Role
        });
    }

    private static string NormalizeRole(string role)
    {
        return string.Equals(role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase)
            ? UserRoles.Admin
            : UserRoles.User;
    }
}

public class UpdateUserRoleRequest
{
    public string Role { get; set; } = string.Empty;
}

public class AdminUserRoleUpdateResponseDto
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = UserRoles.User;
}

public class AdminUserSummaryDto
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = UserRoles.User;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
}
