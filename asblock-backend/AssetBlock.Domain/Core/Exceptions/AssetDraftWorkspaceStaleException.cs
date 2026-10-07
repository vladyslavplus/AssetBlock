namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown inside a transaction when the draft workspace revision no longer matches the expected value.</summary>
public class AssetDraftWorkspaceStaleException() : Exception("The draft workspace changed concurrently.");
