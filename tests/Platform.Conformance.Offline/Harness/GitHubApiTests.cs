using System.Net;
using System.Text;
using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves the GitHub REST shapes used to observe and change the environment repository.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class GitHubApiTests
{
    private const string Token = "<stub-github-token>";

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenGetFileAsync_Base64Content_DecodesItAndSendsTokenAndApiVersion()
    {
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("images:\n  - newTag: 2.5.120\n"));
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json($$"""{ "type": "file", "path": "gitops/workorders/envs/tdd/kustomization.yaml", "sha": "blob1", "content": "{{content[..10]}}\n{{content[10..]}}" }"""));
        using var gitHub = GitHubApi.Create(Token, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        var file = await gitHub.GetFileAsync("example-org/env", "gitops/workorders/envs/tdd/kustomization.yaml", "main");

        file.Content.ShouldBe("images:\n  - newTag: 2.5.120\n");
        file.Sha.ShouldBe("blob1");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.PathAndQuery.ShouldBe("/api/repos/example-org/env/contents/gitops/workorders/envs/tdd/kustomization.yaml?ref=main");
        request.Header("Authorization").ShouldBe($"Bearer {Token}");
        request.Header("X-GitHub-Api-Version").ShouldBe(GitHubApi.ApiVersion);
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCommitFileAsync_ExistingFile_PutsBase64ContentWithItsBlobSha()
    {
        var handler = new StubHttpMessageHandler(request => request.Method == "GET"
            ? StubHttpMessageHandler.Json($$"""{ "type": "file", "path": "README.md", "sha": "blob1", "content": "{{Convert.ToBase64String("old"u8.ToArray())}}" }""")
            : StubHttpMessageHandler.Json("""{ "commit": { "sha": "commit2" } }"""));
        using var gitHub = GitHubApi.Create(Token, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        var commit = await gitHub.CommitFileAsync("example-org/env", "conformance/run-42", "README.md", "new", "Conformance run 42");

        commit.ShouldBe("commit2");
        var put = handler.Requests.Last();
        put.Method.ShouldBe("PUT");
        put.PathAndQuery.ShouldBe("/api/repos/example-org/env/contents/README.md");
        using var body = JsonDocument.Parse(put.Body!);
        body.RootElement.GetProperty("sha").GetString().ShouldBe("blob1");
        body.RootElement.GetProperty("branch").GetString().ShouldBe("conformance/run-42");
        Encoding.UTF8.GetString(Convert.FromBase64String(body.RootElement.GetProperty("content").GetString()!)).ShouldBe("new");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCreateBranchAndOpenPullRequestAsync_Requests_PostRefAndPull()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery.EndsWith("/pulls", StringComparison.Ordinal)
            ? StubHttpMessageHandler.Json("""{ "number": 17, "html_url": "https://github.example.test/example-org/env/pull/17", "head": { "sha": "commit2" } }""", HttpStatusCode.Created)
            : StubHttpMessageHandler.Json("""{ "ref": "refs/heads/conformance/run-42" }""", HttpStatusCode.Created));
        using var gitHub = GitHubApi.Create(Token, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        await gitHub.CreateBranchAsync("example-org/env", "conformance/run-42", "commit1");
        var pull = await gitHub.OpenPullRequestAsync("example-org/env", "conformance/run-42", "main", "Conformance run 42", "Opened by the conformance harness.");

        pull.ShouldBe(new GitHubPullRequest(17, "https://github.example.test/example-org/env/pull/17", "commit2"));
        using var reference = JsonDocument.Parse(handler.Requests[0].Body!);
        reference.RootElement.GetProperty("ref").GetString().ShouldBe("refs/heads/conformance/run-42");
        reference.RootElement.GetProperty("sha").GetString().ShouldBe("commit1");
        using var pullBody = JsonDocument.Parse(handler.Requests[1].Body!);
        pullBody.RootElement.GetProperty("base").GetString().ShouldBe("main");
        pullBody.RootElement.GetProperty("head").GetString().ShouldBe("conformance/run-42");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCreateBranchWithFileAsync_BaseCommit_PostsTreeCommitThenTheReference()
    {
        var handler = new StubHttpMessageHandler(request => request.PathAndQuery switch
        {
            var path when path.EndsWith("/git/commits/base1", StringComparison.Ordinal) => StubHttpMessageHandler.Json("""{ "sha": "base1", "tree": { "sha": "tree1" } }"""),
            var path when path.EndsWith("/git/trees", StringComparison.Ordinal) => StubHttpMessageHandler.Json("""{ "sha": "tree2" }""", HttpStatusCode.Created),
            var path when path.EndsWith("/git/commits", StringComparison.Ordinal) => StubHttpMessageHandler.Json("""{ "sha": "commit2" }""", HttpStatusCode.Created),
            _ => StubHttpMessageHandler.Json("""{ "ref": "refs/heads/e2e/run-42" }""", HttpStatusCode.Created),
        });
        using var gitHub = GitHubApi.Create(Token, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        var sha = await gitHub.CreateBranchWithFileAsync("example-org/app", "e2e/run-42", "base1", "src/e2e-marker.txt", "marker\n", "e2e: marker");

        sha.ShouldBe("commit2");
        handler.Requests.Select(request => $"{request.Method} {request.Uri.AbsolutePath}").ShouldBe(
        [
            "GET /api/repos/example-org/app/git/commits/base1",
            "POST /api/repos/example-org/app/git/trees",
            "POST /api/repos/example-org/app/git/commits",
            "POST /api/repos/example-org/app/git/refs",
        ]);
        using var tree = JsonDocument.Parse(handler.Requests[1].Body!);
        tree.RootElement.GetProperty("base_tree").GetString().ShouldBe("tree1");
        var entry = tree.RootElement.GetProperty("tree")[0];
        entry.GetProperty("path").GetString().ShouldBe("src/e2e-marker.txt");
        entry.GetProperty("mode").GetString().ShouldBe("100644");
        entry.GetProperty("content").GetString().ShouldBe("marker\n");
        using var commit = JsonDocument.Parse(handler.Requests[2].Body!);
        commit.RootElement.GetProperty("tree").GetString().ShouldBe("tree2");
        commit.RootElement.GetProperty("parents")[0].GetString().ShouldBe("base1");
        using var reference = JsonDocument.Parse(handler.Requests[3].Body!);
        reference.RootElement.GetProperty("ref").GetString().ShouldBe("refs/heads/e2e/run-42");
        reference.RootElement.GetProperty("sha").GetString().ShouldBe("commit2");
    }

    [Test]
    [Capability("CAP-HARNESS-009")]
    public async Task WhenCompareAsync_TwoReferences_ReadsStatusCountsAndFiles()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json("""{ "status": "ahead", "ahead_by": 1, "behind_by": 0, "commits": [ { "sha": "c2", "commit": { "message": "Pin 2.5.120", "author": { "name": "platform-bots" }, "committer": { "date": "2026-09-24T05:00:00Z" } } } ], "files": [ { "filename": "gitops/workorders/envs/tdd/kustomization.yaml" } ] }"""));
        using var gitHub = GitHubApi.Create(Token, TimeSpan.FromSeconds(30), handler, "https://github.example.test/api/");

        var comparison = await gitHub.CompareAsync("example-org/env", "c1", "main");

        comparison.Status.ShouldBe("ahead");
        comparison.AheadBy.ShouldBe(1);
        comparison.Commits.ShouldHaveSingleItem().Message.ShouldBe("Pin 2.5.120");
        comparison.Files.ShouldBe(["gitops/workorders/envs/tdd/kustomization.yaml"]);
        handler.Requests.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/api/repos/example-org/env/compare/c1...main");
    }
}
