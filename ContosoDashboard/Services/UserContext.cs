using System.Security.Claims;
using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

// Identity of the requesting user, always built from authenticated claims (never from client input)
public record UserContext(int UserId, UserRole Role, string? Department)
{
    public const string DepartmentClaimType = "Department";

    public bool IsAdministrator => Role == UserRole.Administrator;
    public bool IsTeamLead => Role == UserRole.TeamLead;

    public static UserContext FromPrincipal(ClaimsPrincipal principal)
    {
        var idClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idClaim, out var userId))
        {
            throw new InvalidOperationException("Authenticated user has no valid identifier claim.");
        }

        var role = Enum.TryParse<UserRole>(principal.FindFirst(ClaimTypes.Role)?.Value, out var parsedRole)
            ? parsedRole
            : UserRole.Employee;

        var department = principal.FindFirst(DepartmentClaimType)?.Value;
        return new UserContext(userId, role, string.IsNullOrWhiteSpace(department) ? null : department);
    }
}
