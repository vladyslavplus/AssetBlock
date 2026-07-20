namespace AssetBlock.Domain.Core.Exceptions;

public sealed class ActiveCheckoutIntentException() : Exception("An active checkout intent already exists for this asset and user.");
