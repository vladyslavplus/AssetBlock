using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Application.Tests.Common.Caching;

public sealed class JsonTypedCacheTests
{
    private readonly ICacheService _raw = Substitute.For<ICacheService>();
    private readonly JsonTypedCache _sut;

    private sealed record SampleDto(string Name, int Value);

    private sealed class NotSupportedDto
    {
        [JsonConverter(typeof(ThrowingConverter))]
        public string Name { get; set; } = "";
    }

    private sealed class ThrowingConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("cannot read");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            throw new NotSupportedException("cannot write");
    }

    private sealed class ExplodingDto
    {
        private readonly bool _explode = true;

        public string Name =>
            _explode
                ? throw new InvalidOperationException("serialize boom")
                : string.Empty;
    }

    public JsonTypedCacheTests()
    {
        _sut = new JsonTypedCache(_raw, NullLogger<JsonTypedCache>.Instance);
    }

    [Fact]
    public async Task Get_ValidPayload_Deserializes()
    {
        var json = JsonSerializer.Serialize(
            new SampleDto("a", 1),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        _raw.GetString("k", Arg.Any<CancellationToken>()).Returns(json);

        var result = await _sut.Get<SampleDto>("k");

        result.Should().Be(new SampleDto("a", 1));
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        _raw.GetString("k", Arg.Any<CancellationToken>()).Returns((string?)null);

        (await _sut.Get<SampleDto>("k")).Should().BeNull();
    }

    [Fact]
    public async Task Get_MalformedJson_ReturnsNullAndRemovesKey()
    {
        _raw.GetString("k", Arg.Any<CancellationToken>()).Returns("{not-json");

        (await _sut.Get<SampleDto>("k")).Should().BeNull();
        await _raw.Received(1).Remove("k", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_NotSupportedException_ReturnsNullAndRemovesKey()
    {
        _raw.GetString("k", Arg.Any<CancellationToken>()).Returns("{\"name\":\"x\"}");

        (await _sut.Get<NotSupportedDto>("k")).Should().BeNull();
        await _raw.Received(1).Remove("k", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Get_InfrastructureFailure_ReturnsNull()
    {
        _raw.GetString("k", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        (await _sut.Get<SampleDto>("k")).Should().BeNull();
    }

    [Fact]
    public async Task Get_Cancellation_Rethrows()
    {
        _raw.GetString("k", Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Get<SampleDto>("k");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Set_InfrastructureFailure_DoesNotThrow()
    {
        _raw.SetString("k", Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var act = () => _sut.Set("k", new SampleDto("a", 1), TimeSpan.FromSeconds(1));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Set_SerializationFailure_DoesNotThrow()
    {
        var act = () => _sut.Set("k", new ExplodingDto(), TimeSpan.FromSeconds(1));
        await act.Should().NotThrowAsync();
        await _raw.DidNotReceiveWithAnyArgs()
            .SetString(null!, null!, null, CancellationToken.None);
    }

    [Fact]
    public async Task Set_Cancellation_Rethrows()
    {
        _raw.SetString("k", Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _sut.Set("k", new SampleDto("a", 1), TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
