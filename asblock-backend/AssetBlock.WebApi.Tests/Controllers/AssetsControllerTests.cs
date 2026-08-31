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
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Models;
using AssetBlock.WebApi.Tests.Common;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class AssetsControllerTests : ControllerTestBase
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly IDownloadService _downloadService = Substitute.For<IDownloadService>();
    private readonly IOptions<FileUploadOptions> _fileUploadOptions = Options.Create(new FileUploadOptions());

    private AssetsController CreateController()
    {
        return new AssetsController(Sender, _downloadService, _fileUploadOptions, NullLogger<AssetsController>.Instance);
    }

    [Fact]
    public async Task List_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<GetAssetsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(new DomainPaging.PagedResult<AssetListItem>([], 0, 1, 10))));

        AssetsController controller = CreateController();
        IActionResult result = await controller.List(new GetAssetsRequest(), CancellationToken.None);

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
                "seller",
                DateTimeOffset.UtcNow,
                null,
                Array.Empty<string>(),
                0,
                CurrentVersionNumber: 1,
                CurrentVersionId: Guid.NewGuid(),
                CurrentVersionCreatedAt: DateTimeOffset.UtcNow,
                CurrentFileName: "asset.zip",
                CurrentContentLength: 1024,
                CurrentContentSha256: new string('a', 64),
                CurrentLicense: new AssetLicenseSummaryDto("PERSONAL", "Personal use", "1.0", "Terms text")))));

        AssetsController controller = CreateController();
        IActionResult result = await controller.GetById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Download_WhenNoUser_ShouldReturnUnauthorized()
    {
        AssetsController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Download_WhenNotFound_ShouldReturn404()
    {
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(AssetDownloadStatus.NOT_FOUND));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Download_WhenForbidden_ShouldReturn403()
    {
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(AssetDownloadStatus.FORBIDDEN));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Download_WhenRateLimited_ShouldReturn429()
    {
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(AssetDownloadStatus.RATE_LIMITED));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task Download_WhenSuccess_ShouldStreamBodyAndReturnEmpty()
    {
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(
                AssetDownloadStatus.SUCCESS,
                new DownloadPermit("assets/k", "a.zip")));
        _downloadService.CopyDecrypted(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Stream dest = ci.ArgAt<Stream>(1);
                return dest.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
            });

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        await _downloadService.Received(1).CopyDecrypted("assets/k", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        controller.Response.Headers.AcceptRanges.ToString().Should().Be("none");
        controller.Response.Headers.ContentDisposition.ToString().Should().Contain("filename=a.zip");
    }

    [Fact]
    public async Task Download_WhenNonAsciiFileName_ShouldSetRfc5987ContentDisposition()
    {
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(
                AssetDownloadStatus.SUCCESS,
                new DownloadPermit("assets/k", "тест.zip")));
        _downloadService.CopyDecrypted(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        var cd = controller.Response.Headers.ContentDisposition.ToString();
        cd.Should().Contain("attachment");
        cd.Should().Contain("filename*=");
    }

    [Fact]
    public async Task DownloadVersion_WhenSuccess_ShouldStreamBodyAndSetAttachmentHeader()
    {
        var versionId = Guid.NewGuid();
        _downloadService.AuthorizeDownload(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Is<Guid?>(v => v == versionId), Arg.Any<CancellationToken>())
            .Returns(new DownloadAuthorization(
                AssetDownloadStatus.SUCCESS,
                new DownloadPermit("assets/k2", "v2.zip")));
        _downloadService.CopyDecrypted(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.DownloadVersion(Guid.NewGuid(), versionId, CancellationToken.None);

        result.Should().BeOfType<EmptyResult>();
        await _downloadService.Received(1).CopyDecrypted("assets/k2", Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        controller.Response.Headers.ContentDisposition.ToString().Should().Contain("filename=v2.zip");
    }

    [Fact]
    public async Task Download_WhenRangeHeader_ShouldReturn416()
    {
        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        controller.Request.Headers.Range = "bytes=0-1";

        IActionResult result = await controller.Download(Guid.NewGuid(), CancellationToken.None);

        StatusCodeResult status = result.Should().BeOfType<StatusCodeResult>().Which;
        status.StatusCode.Should().Be(StatusCodes.Status416RangeNotSatisfiable);
        await _downloadService.DidNotReceiveWithAnyArgs()
            .AuthorizeDownload(Guid.Empty, Guid.Empty, Arg.Any<Guid?>(), CancellationToken.None);
    }

    [Fact]
    public async Task Upload_WhenNoUser_ShouldReturnUnauthorized()
    {
        AssetsController controller = CreateController();
        SetupAnonymous(controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.zip"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        IActionResult result = await controller.Upload(form, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Upload_WhenEmptyFile_ShouldReturnBadRequest()
    {
        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream(), 0, 0, "file", "f.zip"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        IActionResult result = await controller.Upload(form, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Upload_WhenFileTooLarge_ShouldReturnBadRequest()
    {
        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        var length = _fileUploadOptions.Value.MaxFileBytes + 1;
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, length, "file", "f.zip"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        IActionResult result = await controller.Upload(form, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Upload_WhenSuccess_ShouldReturnOkWithId()
    {
        var assetId = Guid.NewGuid();
        Sender.Send(Arg.Any<UploadAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(assetId)));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.zip"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        IActionResult result = await controller.Upload(form, CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Upload_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<UploadAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<Guid>.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND)));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        var form = new UploadAssetFormWithFile
        {
            File = new FormFile(new MemoryStream([1]), 0, 1, "file", "f.zip"),
            Title = "t",
            CategoryId = Guid.NewGuid()
        };
        IActionResult result = await controller.Upload(form, CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Update_WhenNoUser_ShouldReturnUnauthorized()
    {
        AssetsController controller = CreateController();
        SetupAnonymous(controller);
        IActionResult result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Update_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(Title: "x"), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Update_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<UpdateAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND)));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Update(Guid.NewGuid(), new UpdateAssetRequest(), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Delete_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<DeleteAssetCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task AddTag_WhenSuccess_ShouldReturnOkWithBody()
    {
        var tag = new TagDto(Guid.NewGuid(), "t");
        Sender.Send(Arg.Any<AddAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(tag)));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.AddTag(Guid.NewGuid(), new AddAssetTagRequest("t"), CancellationToken.None);

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Which;
        ok.Value.Should().Be(tag);
    }

    [Fact]
    public async Task AddTag_WhenFailure_ShouldMapResult()
    {
        Sender.Send(Arg.Any<AddAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TagDto>.Conflict(ErrorCodes.ERR_ASSET_TAG_ALREADY_EXISTS)));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.AddTag(Guid.NewGuid(), new AddAssetTagRequest("t"), CancellationToken.None);

        await AssertStatusCodeAsync(controller, result, StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task RemoveTag_WhenSuccess_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<RemoveAssetTagCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        AssetsController controller = CreateController();
        SetupUser(_userId, controller);
        IActionResult result = await controller.RemoveTag(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }
}
