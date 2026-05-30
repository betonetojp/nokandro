namespace nokandro
{
    /// <summary>Runtime options for NostrBunker (persistence, Amber-style reconnect).</summary>
    public sealed class BunkerOptions
    {
        public Func<string, bool>? IsAuthorized { get; init; }
        public Action<string, string?, bool>? OnClientPaired { get; init; }
        public Func<string, Nip46Permissions?>? GetPermissions { get; init; }
        public Action<string, string?>? SavePermissions { get; init; }
    }
}
