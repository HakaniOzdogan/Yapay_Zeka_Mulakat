using System.Security.Claims;
using InterviewCoach.Domain;

namespace InterviewCoach.Api.Services;

public static class CurrentUserExtensions
{
    public static bool TryGetCurrentUserId(this ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;

        var raw =
            user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        return Guid.TryParse(raw, out userId);
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
    {
        return user.IsInRole(UserRoles.Admin)
            || string.Equals(user.FindFirstValue("role"), UserRoles.Admin, StringComparison.OrdinalIgnoreCase);
    }
}
