namespace Mutter.Web.Models;

public sealed record SelfUpdatePlan(
    string Version,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string ChecksumUrl);
