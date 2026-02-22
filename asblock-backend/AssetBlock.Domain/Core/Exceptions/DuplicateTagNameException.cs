namespace AssetBlock.Domain.Core.Exceptions;

public sealed class DuplicateTagNameException() : Exception("A tag with this name already exists.");
