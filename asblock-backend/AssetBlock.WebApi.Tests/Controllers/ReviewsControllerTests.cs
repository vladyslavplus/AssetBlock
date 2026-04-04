using Ardalis.Result;
using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AssetBlock.Application.UseCases.Reviews.GetReviewById;
using AssetBlock.Application.UseCases.Reviews.GetReviews;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ReviewsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();

    [Fact]
    public async Task CreateReview_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<CreateReviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new ReviewsController(Sender);
        SetupUser(_userId, controller);
        var result = await controller.CreateReview(Guid.NewGuid(), new CreateReviewRequest(5, "ok"), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task CreateReview_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<CreateReviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND)));

        var controller = new ReviewsController(Sender);
        SetupUser(_userId, controller);
        var result = await controller.CreateReview(Guid.NewGuid(), new CreateReviewRequest(5, null), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetReviews_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetReviewsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new DomainPaging.PagedResult<ReviewListItem>([], 0, 1, 10))));

        var controller = new ReviewsController(Sender);
        var result = await controller.GetReviews(Guid.NewGuid(), new GetReviewsRequest(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetReviewById_ShouldReturnOk()
    {
        var detail = new ReviewDetailItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u", 5, null, DateTimeOffset.UtcNow);
        Sender.Send(Arg.Any<GetReviewByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(detail)));

        var controller = new ReviewsController(Sender);
        var result = await controller.GetReviewById(detail.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task DeleteReview_ShouldReturnMappedResult()
    {
        Sender.Send(Arg.Any<DeleteReviewCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new ReviewsController(Sender);
        var result = await controller.DeleteReview(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }
}
