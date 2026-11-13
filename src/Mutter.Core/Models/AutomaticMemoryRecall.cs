namespace Mutter.Core.Models;

public sealed record AutomaticMemoryRecall(string Text, IReadOnlyList<long> Ids)
{
    public static AutomaticMemoryRecall Empty { get; } = new("", Array.Empty<long>());
    public int Count => Ids.Count;
}
