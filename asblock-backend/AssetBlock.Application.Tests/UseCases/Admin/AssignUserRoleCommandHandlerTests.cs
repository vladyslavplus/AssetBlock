using AssetBlock.Application.UseCases.Admin.AssignUserRole;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Users;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Admin;

public sealed class AssignUserRoleCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly AssignUserRoleCommandHandler _handler;

    public AssignUserRoleCommandHandlerTests()
    {
        _unitOfWork.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Func<CancellationToken, Task> action = call.Arg<Func<CancellationToken, Task>>();
                return action(CancellationToken.None);
            });

        _handler = new AssignUserRoleCommandHandler(_userStore, _unitOfWork, _auditWriter);
    }

    [Fact]
    public async Task Handle_WhenAdminAssignsModerator_ShouldIncrementRoleRevision()
    {
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _userStore.LockUsersForRoleUpdateInOrder(adminId, targetId, Arg.Any<CancellationToken>())
            .Returns((
                new UserPersistedRole(adminId, AppRoles.ADMIN, 1),
                new UserPersistedRole(targetId, AppRoles.USER, 2)));
        _userStore.TryAssignRole(targetId, AppRoles.MODERATOR, 2, Arg.Any<CancellationToken>())
            .Returns(true);

        Ardalis.Result.Result<UserRoleAssignmentResult> result = await _handler.Handle(
            new AssignUserRoleCommand(adminId, targetId, AppRoles.MODERATOR, 2),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Role.Should().Be(AppRoles.MODERATOR);
        result.Value.RoleRevision.Should().Be(3);
        await _auditWriter.Received(1).Write(
            Arg.Is<AuditEvent>(e => e.Action == AuditActions.USER_ROLE_ASSIGN),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenTargetIsAdmin_ShouldReturnForbidden()
    {
        var adminId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        _userStore.LockUsersForRoleUpdateInOrder(adminId, targetId, Arg.Any<CancellationToken>())
            .Returns((
                new UserPersistedRole(adminId, AppRoles.ADMIN, 1),
                new UserPersistedRole(targetId, AppRoles.ADMIN, 1)));

        Ardalis.Result.Result<UserRoleAssignmentResult> result = await _handler.Handle(
            new AssignUserRoleCommand(adminId, targetId, AppRoles.MODERATOR, 1),
            CancellationToken.None);

        result.Status.Should().Be(Ardalis.Result.ResultStatus.Forbidden);
        await _userStore.DidNotReceive().TryAssignRole(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }
}
