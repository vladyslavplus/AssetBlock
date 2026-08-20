namespace AssetBlock.WebApi.Services;

public interface IAnalyticsBffSignatureValidator
{
    AnalyticsBffSignatureValidationResult Validate(HttpContext httpContext);
}
