namespace Mutter.Web.Models;

public sealed record SelfUpdatePlan(
    string Version,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string Kind,
    string? ExpectedSha256,
    string? ChecksumUrl,
    string? RuntimeVersion,
    bool AllowAnyHttps);
