using Ardalis.Result;
using AssetBlock.Application.UseCases.Categories.CreateCategory;
using AssetBlock.Application.UseCases.Categories.DeleteCategory;
using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Application.UseCases.Categories.GetCategoryById;
using AssetBlock.Application.UseCases.Categories.UpdateCategory;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NoValueResult = Ardalis.Result.Result;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class CategoriesControllerTests : ControllerTestBase
{
    [Fact]
    public async Task Get_WhenNullRequest_ShouldUseDefaultAndReturnOk()
    {
        Sender.Send(Arg.Any<GetCategoriesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(new DomainPaging.PagedResult<CategoryListItem>([], 0, 1, 10))));

        var controller = new CategoriesController(Sender);
        var result = await controller.Get(null, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetById_ShouldReturnOk()
    {
        var item = new CategoryResponse(Guid.NewGuid(), "n", "s", null);
        Sender.Send(Arg.Any<GetCategoryByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(item)));

        var controller = new CategoriesController(Sender);
        var result = await controller.GetById(item.Id, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_WhenSuccess_ShouldReturn201()
    {
        var created = new CreateCategoryResponse(Guid.NewGuid());
        Sender.Send(Arg.Any<CreateCategoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success(created)));

        var controller = new CategoriesController(Sender);
        var result = await controller.Create(new CreateCategoryRequest("n", null, "slug"), CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Update_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<UpdateCategoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new CategoriesController(Sender);
        var id = Guid.NewGuid();
        var result = await controller.Update(id, new UpdateCategoryRequest("n", null, "s"), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }

    [Fact]
    public async Task Delete_ShouldReturnOk()
    {
        Sender.Send(Arg.Any<DeleteCategoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(NoValueResult.Success()));

        var controller = new CategoriesController(Sender);
        var result = await controller.Delete(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkResult>();
    }
}
