namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when user creation fails due to duplicate email (e.g. unique constraint).</summary>
public sealed class DuplicateEmailException() : Exception("An account with this email already exists.");
