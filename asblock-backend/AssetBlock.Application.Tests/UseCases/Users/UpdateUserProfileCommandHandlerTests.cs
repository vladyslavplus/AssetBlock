using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class UpdateUserProfileCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly UpdateUserProfileCommandHandler _handler;

    public UpdateUserProfileCommandHandlerTests()
    {
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateUserProfileCommandHandler(
            _userStore,
            _unitOfWorkMock,
            _auditWriterMock,
            NullLogger<UpdateUserProfileCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenUserMissing_ShouldReturnNotFound()
    {
        var id = Guid.NewGuid();
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new UpdateUserProfileCommand(id, "x", null, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_USER_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenDuplicateUsername_ShouldReturnConflict()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = "old",
            Email = "e@e.com",
            PasswordHash = "h",
            Role = AppRoles.USER
        };
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns(user);
        _userStore
            .Update(Arg.Any<User>(), Arg.Any<CancellationToken>())
            .Returns<Task<User>>(_ => throw new DuplicateUsernameException());

        var result = await _handler.Handle(new UpdateUserProfileCommand(id, "taken", null, null, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Status.Should().Be(Ardalis.Result.ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
    }

    [Fact]
    public async Task Handle_WhenOk_ShouldReturnUpdatedDtoAndWriteAuditInsideTransaction()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = "old",
            Email = "e@e.com",
            PasswordHash = "h",
            Role = AppRoles.USER
        };
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns(user);
        _userStore.Update(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<User>());

        var result = await _handler.Handle(new UpdateUserProfileCommand(id, "newname", null, "bio", true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Username.Should().Be("newname");
        result.Value.Bio.Should().Be("bio");
        result.Value.IsPublicProfile.Should().BeTrue();

        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.USER_PROFILE_UPDATE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceType == AuditResourceTypes.USER &&
                e.ResourceId == id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAvatarAndBioAreWhitespace_ShouldClearToNull()
    {
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = "u",
            Email = "e@e.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            AvatarUrl = "https://old",
            Bio = "old"
        };
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns(user);
        _userStore.Update(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(callInfo => callInfo.Arg<User>());

        var result = await _handler.Handle(new UpdateUserProfileCommand(id, null, "   ", "  \t ", null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AvatarUrl.Should().BeNull();
        result.Value.Bio.Should().BeNull();
    }
}
