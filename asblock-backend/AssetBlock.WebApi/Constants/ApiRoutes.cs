namespace AssetBlock.WebApi.Constants;

/// <summary>
/// API route segments. Use with [Route], [HttpPost(Routes.X)], etc.
/// </summary>
public static class ApiRoutes
{
    public static class Auth
    {
        public const string LOGIN = "login";
        public const string REFRESH = "refresh";
        public const string REGISTER = "register";
    }

    public static class Categories
    {
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
    }

    public static class Assets
    {
        public const string LIST = "";
        public const string UPLOAD = "upload";
        public const string ID = "{id:guid}";
        public const string DOWNLOAD = "{id:guid}/download";
        public const string TAGS = "{id:guid}/tags";
        public const string TAGS_ID = "{id:guid}/tags/{tagId:guid}";
    }

    public static class Payments
    {
        public const string CHECKOUT = "checkout";
        public const string WEBHOOK = "webhook";
    }

    public static class Reviews
    {
        public const string LIST_FOR_ASSET = "assets/{assetId:guid}/reviews";
        public const string CREATE_FOR_ASSET = "assets/{assetId:guid}/reviews";
        public const string BY_ID = "reviews/{id:guid}";
    }

    public static class Tags
    {
        public const string BASE = "";
        public const string ID = "{id:guid}";
    }
}
