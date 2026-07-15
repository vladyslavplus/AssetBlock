namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when an uploaded archive fails security or policy inspection.</summary>
public sealed class ArchiveRejectedException(string message) : Exception(message);
