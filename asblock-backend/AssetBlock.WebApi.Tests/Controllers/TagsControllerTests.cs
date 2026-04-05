using Ardalis.Result;
using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Application.UseCases.Tags.DeleteTag;
using AssetBlock.Application.UseCases.Tags.GetTagById;
using AssetBlock.Application.UseCases.Tags.GetTags;
using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class TagsControllerTests : ControllerTestBase
{
    [Fact]
    public async Task SearchTags_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new DomainPaging.PagedResult<TagDto>([], 0, 1, 10))));

        var controller = new TagsController(Sender);
        var result = await controller.SearchTags(new GetTagsRequest(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetById_ShouldReturnOk()
    {
        var tag = new TagDto(Guid.NewGuid(), "name");
        Sender.Send(Arg.Any<GetTagByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(tag)));

        var controller = new TagsController(Sender);
        var result = await controller.GetById(tag.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_WhenSuccess_ShouldReturn201()
    {
        var tag = new TagDto(Guid.NewGuid(), "new");
        Sender.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(tag)));

        var controller = new TagsController(Sender);
        var result = await controller.Create(new CreateTagCommand("new"), CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<CreateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TagDto>.Conflict(ErrorCodes.ERR_TAG_ALREADY_EXISTS)));

        var controller = new TagsController(Sender);
        var result = await controller.Create(new CreateTagCommand("x"), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Which;
        obj.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Update_ShouldReturnOk()
    {
        var tag = new TagDto(Guid.NewGuid(), "u");
        Sender.Send(Arg.Any<UpdateTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(tag)));

        var controller = new TagsController(Sender);
        var result = await controller.Update(tag.Id, new UpdateTagRequest("u"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Delete_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new TagsController(Sender);
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Delete_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<DeleteTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.NotFound(ErrorCodes.ERR_TAG_NOT_FOUND)));

        var controller = new TagsController(Sender);
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}
