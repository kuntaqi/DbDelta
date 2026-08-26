namespace DbDelta.Core.Model;

public sealed record IndexColumn(string Name, bool IsDescending = false)
{
    public override string ToString() => IsDescending ? $"{Name} DESC" : Name;
}
