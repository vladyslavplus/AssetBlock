namespace AssetBlock.Domain.Core.Exceptions;

public sealed class DuplicateCollectionAssetException()
    : Exception("This asset is already in the collection.");
