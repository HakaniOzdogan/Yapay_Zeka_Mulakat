namespace InterviewCoach.Domain;

public static class UserRoles
{
    public const string User = "User";
    public const string Admin = "Admin";

    public static bool IsValid(string? role)
    {
        return string.Equals(role, User, StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);
    }
}
