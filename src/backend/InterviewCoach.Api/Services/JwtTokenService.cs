using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using InterviewCoach.Application;
using InterviewCoach.Domain;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InterviewCoach.Api.Services;

public interface IJwtTokenService
{
    string CreateAccessToken(User user);
}

public class JwtTokenService : IJwtTokenService
{
    private readonly AuthOptions _authOptions;

    public JwtTokenService(IOptions<AuthOptions> authOptions)
    {
        _authOptions = authOptions.Value;
    }

    public string CreateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_authOptions.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(_authOptions.AccessTokenMinutes > 0 ? _authOptions.AccessTokenMinutes : 120);

        var resolvedRole = UserRoles.IsValid(user.Role) ? user.Role : UserRoles.User;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, resolvedRole),
            new("role", resolvedRole)
        };

        var token = new JwtSecurityToken(
            issuer: _authOptions.JwtIssuer,
            audience: _authOptions.JwtAudience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
