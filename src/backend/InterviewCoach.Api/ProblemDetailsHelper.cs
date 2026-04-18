using Microsoft.AspNetCore.Mvc;

namespace InterviewCoach.Api;

public static class ProblemDetailsHelper
{
    public static ObjectResult Problem(
        this ControllerBase controller,
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

        problem.Extensions["traceId"] = controller.HttpContext.TraceIdentifier;

        return new ObjectResult(problem)
        {
            StatusCode = statusCode
        };
    }

    public static ObjectResult ValidationProblem(
        this ControllerBase controller,
        string detail,
        IDictionary<string, string[]> errors)
    {
        var problem = new ProblemDetails
        {
            Title = "Validation failed",
            Status = StatusCodes.Status400BadRequest,
            Detail = detail,
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };

        problem.Extensions["errors"] = errors;
        problem.Extensions["traceId"] = controller.HttpContext.TraceIdentifier;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    }

    public static ObjectResult NotFoundProblem(this ControllerBase controller, string detail)
    {
        return controller.Problem(
            StatusCodes.Status404NotFound,
            "Not found",
            detail);
    }

    public static ObjectResult ForbiddenProblem(this ControllerBase controller, string detail)
    {
        return controller.Problem(
            StatusCodes.Status403Forbidden,
            "Forbidden",
            detail);
    }

    public static ObjectResult UnauthorizedProblem(this ControllerBase controller, string detail)
    {
        return controller.Problem(
            StatusCodes.Status401Unauthorized,
            "Unauthorized",
            detail);
    }

    public static ObjectResult ConflictProblem(this ControllerBase controller, string detail)
    {
        return controller.Problem(
            StatusCodes.Status409Conflict,
            "Conflict",
            detail);
    }
}
