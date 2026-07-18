using AssetBlock.Domain.Core.Dto.Email;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Provider-neutral transactional email transport.</summary>
public interface IEmailSender
{
    Task Send(EmailMessage message, CancellationToken cancellationToken = default);
}
