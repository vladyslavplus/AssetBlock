namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when category creation fails due to duplicate slug (e.g. unique constraint).</summary>
public sealed class DuplicateSlugException() : Exception("A category with this slug already exists.");
