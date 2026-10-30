namespace DosboxEnhanced;
internal static class ReleaseUpdateTests
{
    public static void Run(Action<bool, string> check)
    {
        string Feed(string tag, string extra = "") => "[{\"draft\":false,\"published_at\":\"2026-10-30T10:00:00Z\",\"tag_name\":\"" + tag + "\"" + extra + "}]";
        check(ReleaseUpdates.Newer(Feed("v1.0-beta"), "1.0.0-beta") is null, "same normalized beta is not an update");
        check(ReleaseUpdates.Newer(Feed("v1.0-beta.2"), "1.0-beta") == "v1.0-beta.2", "numbered beta is an update");
        check(ReleaseUpdates.Newer(Feed("v1.0-beta.10"), "1.0-beta.2") is not null, "beta numbers use numeric precedence");
        check(ReleaseUpdates.Newer(Feed("v1.0-beta.2"), "1.0-beta.10") is null, "older beta never creates a notice");
        check(ReleaseUpdates.Newer(Feed("v1.0.0"), "1.0-beta") is not null, "stable release succeeds its beta");
        check(ReleaseUpdates.Newer(Feed("v1.0-beta"), "1.0.0") is null, "stable install does not offer its earlier beta");
        check(ReleaseUpdates.Newer(Feed("v2.0-beta", ",\"draft\":true"), "1.0-beta") is null, "unpublished drafts are excluded");
        check(ReleaseUpdates.Newer("{\"message\":\"API limit\"}", "1.0-beta") is null, "API errors stay quiet");
        check(ReleaseUpdates.Newer(Feed("../../evil"), "1.0-beta") is null, "unsafe release tags are rejected");
        check(ReleaseUpdates.Newer("[null,5]", "1.0-beta") is null, "malformed entries stay quiet");
        check(ReleaseUpdates.Newer("[", "1.0-beta") is null, "truncated feed stays quiet");
        check(ReleaseUpdates.Newer(new string(' ', 256 * 1024 + 1), "1.0-beta") is null, "oversized feed stays quiet");
    }
}
