namespace AssetBlock.WebApi.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class PrivateNoStoreAttribute : Attribute;
