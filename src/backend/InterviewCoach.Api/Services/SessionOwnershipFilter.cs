using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Services;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SessionOwnershipAttribute : TypeFilterAttribute
{
    public SessionOwnershipAttribute(string routeParameterName = "sessionId")
        : base(typeof(SessionOwnershipFilter))
    {
        Arguments = [routeParameterName];
    }
}

public class SessionOwnershipFilter : IAsyncActionFilter
{
    private readonly ApplicationDbContext _db;
    private readonly string _routeParameterName;

    public SessionOwnershipFilter(ApplicationDbContext db, string routeParameterName)
    {
        _db = db;
        _routeParameterName = routeParameterName;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!context.HttpContext.User.TryGetCurrentUserId(out var currentUserId))
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status401Unauthorized,
                "Unauthorized",
                "Invalid authenticated user context.");
            return;
        }

        var isAdmin = context.HttpContext.User.IsAdmin();

        var rawRouteValue = context.RouteData.Values[_routeParameterName]?.ToString();
        if (!Guid.TryParse(rawRouteValue, out var sessionId))
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status400BadRequest,
                "Validation failed",
                $"Route parameter '{_routeParameterName}' must be a valid GUID.");
            return;
        }

        var sessionInfo = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.Id,
                s.UserId
            })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (sessionInfo == null)
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status404NotFound,
                "Not found",
                $"Session '{sessionId}' was not found.");
            return;
        }

        if (isAdmin)
        {
            await next();
            return;
        }

        if (!sessionInfo.UserId.HasValue || sessionInfo.UserId.Value != currentUserId)
        {
            context.Result = CreateProblem(
                context,
                StatusCodes.Status403Forbidden,
                "Forbidden",
                "You do not have access to this session.");
            return;
        }

        await next();
    }

    private static ObjectResult CreateProblem(
        ActionExecutingContext context,
        int statusCode,
        string title,
        string detail)
    {
        var problem = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = detail,
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        return new ObjectResult(problem)
        {
            StatusCode = statusCode
        };
    }
}
