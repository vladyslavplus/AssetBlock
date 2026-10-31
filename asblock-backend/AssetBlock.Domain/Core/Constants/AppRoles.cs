using System.Linq.Expressions;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Application role names. Use with JWT generation.
/// </summary>
public static class AppRoles
{
    public const string ADMIN = "Admin";
    public const string MODERATOR = "Moderator";
    public const string USER = "User";

    public static bool CanActAsOwner(string? role) => role is USER or MODERATOR or ADMIN;
    public static readonly Expression<Func<string?, bool>> CanReviewExpression = role => role == MODERATOR;
    private static readonly Func<string?, bool> _canReview = CanReviewExpression.Compile();
    public static readonly Expression<Func<User, bool>> CanReviewUserExpression = BuildUserPredicate();
    public static bool CanReview(string? role) => _canReview(role);

    private static Expression<Func<User, bool>> BuildUserPredicate()
    {
        ParameterExpression user = Expression.Parameter(typeof(User), "user");
        Expression body = new ReplaceRoleParameter(CanReviewExpression.Parameters[0], Expression.Property(user, nameof(User.Role))).Visit(CanReviewExpression.Body)!;
        return Expression.Lambda<Func<User, bool>>(body, user);
    }

    private sealed class ReplaceRoleParameter(ParameterExpression source, Expression target) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == source ? target : base.VisitParameter(node);
    }

    /// <summary>Roles assignable through the scoped Admin User/Moderator endpoint.</summary>
    public static readonly IReadOnlySet<string> AdminAssignableRoles =
        new HashSet<string>(StringComparer.Ordinal) { USER, MODERATOR };
}
