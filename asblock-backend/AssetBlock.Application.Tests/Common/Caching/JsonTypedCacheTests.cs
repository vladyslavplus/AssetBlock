using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AwesomeAssertions;
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

        SampleDto? result = await _sut.Get<SampleDto>("k");

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

        Func<Task<SampleDto?>> act = () => _sut.Get<SampleDto>("k");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Set_InfrastructureFailure_DoesNotThrow()
    {
        _raw.SetString("k", Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        Func<Task> act = () => _sut.Set("k", new SampleDto("a", 1), TimeSpan.FromSeconds(1));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Set_SerializationFailure_DoesNotThrow()
    {
        Func<Task> act = () => _sut.Set("k", new ExplodingDto(), TimeSpan.FromSeconds(1));
        await act.Should().NotThrowAsync();
        await _raw.DidNotReceiveWithAnyArgs()
            .SetString(null!, null!, null, CancellationToken.None);
    }

    [Fact]
    public async Task Set_Cancellation_Rethrows()
    {
        _raw.SetString("k", Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        Func<Task> act = () => _sut.Set("k", new SampleDto("a", 1), TimeSpan.FromSeconds(1));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

public sealed class AssetEncryptUploadServiceTests
{
    private readonly IEncryptionService _encryptionService = Substitute.For<IEncryptionService>();
    private readonly IAssetStorageService _assetStorageService = Substitute.For<IAssetStorageService>();
    private readonly AssetEncryptUploadService _sut;

    public AssetEncryptUploadServiceTests()
    {
        _sut = new AssetEncryptUploadService(_encryptionService, _assetStorageService);
    }

    [Fact]
    public async Task EncryptAndUpload_WhenSuccessful_ReturnsLowercaseSha256OfPlaintext()
    {
        var plainBytes = "Hello World Plaintext"u8.ToArray();
        var plainStream = new MemoryStream(plainBytes);
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plainBytes)).ToLowerInvariant();

        _encryptionService.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                Stream input = callInfo.ArgAt<Stream>(0);
                Stream output = callInfo.ArgAt<Stream>(1);
                var buffer = new byte[1024];
                int read;
                while ((read = await input.ReadAsync(buffer, callInfo.ArgAt<CancellationToken>(2))) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), callInfo.ArgAt<CancellationToken>(2));
                }
            });

        _assetStorageService.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                Stream stream = callInfo.ArgAt<Stream>(1);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, callInfo.ArgAt<CancellationToken>(3));
            });

        var hash = await _sut.EncryptAndUpload(plainStream, "key/1", 100, CancellationToken.None);

        hash.Should().Be(expectedSha256);
        await _assetStorageService.Received(1).Upload("key/1", Arg.Any<Stream>(), 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EncryptAndUpload_WhenCancelled_ThrowsOperationCanceledException()
    {
        var plainStream = new MemoryStream(new byte[100]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task<string>> act = () => _sut.EncryptAndUpload(plainStream, "key/1", 100, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EncryptAndUpload_WhenCallerCancelsWhileActive_CancelsBothLegsAndCompletesDeterministically()
    {
        var plainStream = new MemoryStream(new byte[1024]);
        using var cts = new CancellationTokenSource();
        var encryptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encryptionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _encryptionService.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                CancellationToken ct = ci.ArgAt<CancellationToken>(2);
                encryptionStarted.TrySetResult();
                await using CancellationTokenRegistration reg = ct.Register(() => encryptionCancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });

        _assetStorageService.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                CancellationToken ct = ci.ArgAt<CancellationToken>(3);
                uploadStarted.TrySetResult();
                await using CancellationTokenRegistration reg = ct.Register(() => uploadCancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });

        Task<string> serviceTask = _sut.EncryptAndUpload(plainStream, "key/1", 1024, cts.Token);

        var bothStarted = Task.WhenAll(encryptionStarted.Task, uploadStarted.Task);
        Task startWinner = await Task.WhenAny(bothStarted, Task.Delay(2000));
        startWinner.Should().Be(bothStarted, "both encryption and upload legs must start within timeout");

        await cts.CancelAsync();

        Task completionWinner = await Task.WhenAny(serviceTask, Task.Delay(2000));
        completionWinner.Should().Be(serviceTask, "service task must complete promptly upon cancellation");

        Func<Task<string>> act = () => serviceTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        (await Task.WhenAny(encryptionCancelled.Task, Task.Delay(2000))).Should().Be(encryptionCancelled.Task);
        (await Task.WhenAny(uploadCancelled.Task, Task.Delay(2000))).Should().Be(uploadCancelled.Task);
    }

    [Fact]
    public async Task EncryptAndUpload_WhenEncryptionFailsWhileUploadActive_CancelsUploadAndPropagatesException()
    {
        var plainStream = new MemoryStream(new byte[1024]);
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploadCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _encryptionService.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await uploadStarted.Task;
                throw new InvalidOperationException("Encryption error");
            });

        _assetStorageService.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                CancellationToken ct = ci.ArgAt<CancellationToken>(3);
                uploadStarted.TrySetResult();
                using CancellationTokenRegistration reg = ct.Register(() => uploadCancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });

        Task<string> serviceTask = _sut.EncryptAndUpload(plainStream, "key/1", 1024, CancellationToken.None);

        Task startWinner = await Task.WhenAny(uploadStarted.Task, Task.Delay(2000));
        startWinner.Should().Be(uploadStarted.Task, "upload leg must start within timeout");

        Task completionWinner = await Task.WhenAny(serviceTask, Task.Delay(2000));
        completionWinner.Should().Be(serviceTask, "service task must complete promptly upon encryption failure");

        Func<Task<string>> act = () => serviceTask;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Encryption error");

        (await Task.WhenAny(uploadCancelled.Task, Task.Delay(2000))).Should().Be(uploadCancelled.Task);
    }

    [Fact]
    public async Task EncryptAndUpload_WhenUploadFailsWhileEncryptionActive_CancelsEncryptionAndPropagatesException()
    {
        var plainStream = new MemoryStream(new byte[1024]);
        var encryptionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var encryptionCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _encryptionService.Encrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                CancellationToken ct = ci.ArgAt<CancellationToken>(2);
                encryptionStarted.TrySetResult();
                await using CancellationTokenRegistration reg = ct.Register(() => encryptionCancelled.TrySetResult());
                await Task.Delay(Timeout.Infinite, ct);
            });

        _assetStorageService.Upload(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await encryptionStarted.Task;
                throw new InvalidOperationException("Storage error");
            });

        Task<string> serviceTask = _sut.EncryptAndUpload(plainStream, "key/1", 1024, CancellationToken.None);

        Task startWinner = await Task.WhenAny(encryptionStarted.Task, Task.Delay(2000));
        startWinner.Should().Be(encryptionStarted.Task, "encryption leg must start within timeout");

        Task completionWinner = await Task.WhenAny(serviceTask, Task.Delay(2000));
        completionWinner.Should().Be(serviceTask, "service task must complete promptly upon upload failure");

        Func<Task<string>> act = () => serviceTask;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Storage error");

        (await Task.WhenAny(encryptionCancelled.Task, Task.Delay(2000))).Should().Be(encryptionCancelled.Task);
    }
}
