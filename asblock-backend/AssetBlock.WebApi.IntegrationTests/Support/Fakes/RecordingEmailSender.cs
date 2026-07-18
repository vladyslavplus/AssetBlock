using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Email;

namespace AssetBlock.WebApi.IntegrationTests.Support.Fakes;

/// <summary>Test-only recording sender for WebApi DI; not a production fallback.</summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToArray();
            }
        }
    }

    public Task Send(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
