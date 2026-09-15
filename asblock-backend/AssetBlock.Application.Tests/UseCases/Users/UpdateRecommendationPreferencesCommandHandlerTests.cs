using Ardalis.Result;
using AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Users;

public class UpdateRecommendationPreferencesCommandHandlerTests
{
    private readonly IUserStore _userStore = Substitute.For<IUserStore>();
    private readonly IRecommendationPersonalizationStore _personalizationStore = Substitute.For<IRecommendationPersonalizationStore>();
    private readonly IUnitOfWork _unitOfWorkMock;
    private readonly IAuditWriter _auditWriterMock;
    private readonly UpdateRecommendationPreferencesCommandHandler _handler;

    public UpdateRecommendationPreferencesCommandHandlerTests()
    {
        _unitOfWorkMock = Substitute.For<IUnitOfWork>();
        _auditWriterMock = Substitute.For<IAuditWriter>();

        _unitOfWorkMock.ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));

        _handler = new UpdateRecommendationPreferencesCommandHandler(
            _userStore,
            _personalizationStore,
            _unitOfWorkMock,
            _auditWriterMock,
            TimeProvider.System,
            NullLogger<UpdateRecommendationPreferencesCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenUserMissing_ShouldReturnNotFound()
    {
        var id = Guid.NewGuid();
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns((User?)null);

        Result<RecommendationPreferencesDto> result = await _handler.Handle(new UpdateRecommendationPreferencesCommand(id, true), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(ErrorCodes.ERR_USER_NOT_FOUND);
        await _personalizationStore.DidNotReceive().SetPersonalized(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOptIn_ShouldPersistAndWriteAuditInsideTransaction()
    {
        var id = Guid.NewGuid();
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns(MakeUser(id));
        _personalizationStore.SetPersonalized(id, true, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new UserRecommendationPreferences { UserId = id, IsPersonalized = true, OptedInAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        Result<RecommendationPreferencesDto> result = await _handler.Handle(new UpdateRecommendationPreferencesCommand(id, true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPersonalized.Should().BeTrue();
        result.Value.OptedInAt.Should().NotBeNull();
        await _unitOfWorkMock.Received(1).ExecuteInTransaction(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _auditWriterMock.Received(1).Write(
            Arg.Is<AuditEvent>(e =>
                e.Action == AuditActions.USER_RECOMMENDATION_PREFERENCES_UPDATE &&
                e.Outcome == AuditOutcome.SUCCESS &&
                e.ResourceType == AuditResourceTypes.USER &&
                e.ResourceId == id.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOptOut_ShouldPersistOffState()
    {
        var id = Guid.NewGuid();
        _userStore.GetByIdForUpdate(id, Arg.Any<CancellationToken>()).Returns(MakeUser(id));
        _personalizationStore.SetPersonalized(id, false, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new UserRecommendationPreferences { UserId = id, IsPersonalized = false, OptedInAt = null, UpdatedAt = DateTimeOffset.UtcNow });

        Result<RecommendationPreferencesDto> result = await _handler.Handle(new UpdateRecommendationPreferencesCommand(id, false), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPersonalized.Should().BeFalse();
        result.Value.OptedInAt.Should().BeNull();
    }

    private static User MakeUser(Guid id) => new()
    {
        Id = id,
        Username = "u",
        Email = "e@e.com",
        PasswordHash = "h",
        Role = AppRoles.USER
    };
}
