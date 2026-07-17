using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class AuditLogsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task Get_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/admin/audit-logs?page=1&pageSize=20", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_WhenNonAdmin_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri("/api/admin/audit-logs?page=1&pageSize=20", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_WhenAdmin_ShouldReturn200WithPagedShape()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri("/api/admin/audit-logs?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var page = await response.Content.ReadFromJsonAsync<PagedAuditLogsResponse>(_jsonOptions);
        page.Should().NotBeNull();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.Items.Should().NotBeNull();
        page.TotalCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Get_WhenInvalidDateRange_ShouldReturnProblemDetails()
    {
        var (client, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        var from = Uri.EscapeDataString("2026-07-15T00:00:00Z");
        var to = Uri.EscapeDataString("2026-07-01T00:00:00Z");
        var response = await client.GetAsync(
            new Uri($"/api/admin/audit-logs?page=1&pageSize=20&from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("From must be less than or equal to To");
    }

    [Fact]
    public async Task CreateCategory_WhenAdmin_ShouldPersistMatchingAuditRow()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        var slug = $"audit-cat-{Guid.NewGuid():N}"[..24];
        var createResponse = await client.PostAsJsonAsync(
            new Uri("/api/categories", UriKind.Relative),
            new CreateCategoryRequest($"Audit Category {slug}", "audit test", slug));

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateCategoryResponseDto>(_jsonOptions);
        created.Should().NotBeNull();

        var listResponse = await client.GetAsync(
            new Uri(
                $"/api/admin/audit-logs?page=1&pageSize=20&action={Uri.EscapeDataString(AuditActions.CATEGORY_CREATE)}&resourceId={created.Id}",
                UriKind.Relative));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await listResponse.Content.ReadFromJsonAsync<PagedAuditLogsResponse>(_jsonOptions);
        page.Should().NotBeNull();
        page.Items.Should().ContainSingle();
        var item = page.Items[0];
        item.Action.Should().Be(AuditActions.CATEGORY_CREATE);
        item.Outcome.Should().Be("SUCCESS");
        item.ResourceType.Should().Be(AuditResourceTypes.CATEGORY);
        item.ResourceId.Should().Be(created.Id.ToString());
        item.TraceId.Should().NotBeNullOrWhiteSpace();
        item.Metadata.Should().BeNull();

        var raw = await listResponse.Content.ReadAsStringAsync();
        raw.Should().NotContain("MetadataJson");

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.CATEGORY_CREATE && a.ResourceId == created.Id.ToString());
        row.TraceId.Should().Be(item.TraceId);
    }

    private sealed record PagedAuditLogsResponse(
        IReadOnlyList<AuditLogListItemDto> Items,
        int TotalCount,
        int Page,
        int PageSize);

    private sealed record AuditLogListItemDto(
        long Id,
        DateTimeOffset OccurredAt,
        string ActorType,
        Guid? ActorUserId,
        string Action,
        string Outcome,
        string ResourceType,
        string? ResourceId,
        string? TraceId,
        string? IpAddress,
        string? UserAgent,
        JsonElement? Metadata);

    private sealed record CreateCategoryResponseDto(Guid Id);
}
