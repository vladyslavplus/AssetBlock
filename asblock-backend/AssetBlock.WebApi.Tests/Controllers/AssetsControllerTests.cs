using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Application.UseCases.Assets.GetAssetById;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Models;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class AssetsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IDownloadService _downloadService = Substitute.For<IDownloadService>();

    private AssetsController CreateController()
    {
        return new AssetsController(Sender, _downloadService, NullLogger<AssetsController>.Instance);
    }

    [Fact]
    public async Task List_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new DomainPaging.PagedResult<AssetListItem>([], 0, 1, 10))));

        var controller = CreateController();
        var result = await controller.List(new GetAssetsRequest(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetById_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetAssetByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new AssetDetailItem(
                Guid.NewGuid(),
                "t",
                null,
                1m,
                Guid.NewGuid(),
                "c",
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                null))));

        var controller = CreateController();
        var result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Download_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = CreateController();
        SetupAnonymous(controller);
        var result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Download_WhenNotFound_ShouldReturn404()
    {
        _downloadService.GetAssetStream(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AssetDownloadResult(AssetDownloadStatus.NotFound, null, null));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Download_WhenForbidden_ShouldReturn403()
    {
        _downloadService.GetAssetStream(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AssetDownloadResult(AssetDownloadStatus.Forbidden, null, null));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Which;
        obj.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Download_WhenRateLimited_ShouldReturn429()
    {
        _downloadService.GetAssetStream(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AssetDownloadResult(AssetDownloadStatus.RateLimited, null, null));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Which;
        obj.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Download_WhenSuccess_ShouldReturnFile()
    {
        await using var stream = new MemoryStream([1, 2, 3]);
        _downloadService.GetAssetStream(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new AssetDownloadResult(AssetDownloadStatus.Success, stream, "a.bin"));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<FileStreamResult>();
    }

    [Fact]
    public async Task Upload_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = CreateController();
        SetupAnonymous(controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.bin"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        var result = await controller.Upload(form, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Upload_WhenEmptyFile_ShouldReturnBadRequest()
    {
        var controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream(), 0, 0, "file", "f.bin"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        var result = await controller.Upload(form, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Upload_WhenSuccess_ShouldReturnOkWithId()
    {
        var assetId = Guid.NewGuid();
        Sender.Send(Arg.Any<UploadAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(assetId)));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.bin"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        var result = await controller.Upload(form, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Upload_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<UploadAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Guid>.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND)));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.bin"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        var result = await controller.Upload(form, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Update_WhenNoUser_ShouldReturnUnauthorized()
    {
        var controller = CreateController();
        SetupAnonymous(controller);
        var result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(), CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Update_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(Title: "x"), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Update_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<UpdateAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND)));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Delete_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<DeleteAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task AddTag_WhenSuccess_ShouldReturnOkWithBody()
    {
        var tag = new TagDto(Guid.NewGuid(), "t");
        Sender.Send(Arg.Any<AddAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(tag)));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.AddTag(Guid.NewGuid(), new AddAssetTagRequest("t"), CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().Be(tag);
    }

    [Fact]
    public async Task AddTag_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<AddAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TagDto>.Conflict(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS)));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.AddTag(Guid.NewGuid(), new AddAssetTagRequest("t"), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Which;
        obj.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task RemoveTag_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<RemoveAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = CreateController();
        SetupUser(_userId, controller);
        var result = await controller.RemoveTag(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }
}
