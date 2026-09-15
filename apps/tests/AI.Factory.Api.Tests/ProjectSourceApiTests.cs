using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Phase 18.16 §18/§19 — HTTP-level coverage for POST /api/projects/{id}/source.
/// Uses a local, deterministic bare Git repository fixture (real `git`
/// commands, no GitHub/internet dependency) for the success/failure paths
/// that need a real repository, exactly like ProjectSourceInitializerTests.
/// </summary>
public sealed class ProjectSourceApiTests : IClassFixture<ApiTestFactory>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempRoot;
    private readonly string _bareRepoPath;

    public ProjectSourceApiTests(ApiTestFactory factory)
    {
        _client = factory.CreateClient();
        _tempRoot = Path.Combine(Path.GetTempPath(), "aifapi", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        _bareRepoPath = Path.Combine(_tempRoot, "fx.git");
        CreateBareFixtureRepository();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private void CreateBareFixtureRepository()
    {
        var seedDir = Path.Combine(_tempRoot, "seed");
        Directory.CreateDirectory(seedDir);

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "test@example.com");
        RunGit(seedDir, "config", "user.name", "Test");
        RunGit(seedDir, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(seedDir, "main.txt"), "main content");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "initial commit");

        RunGit(_tempRoot, "clone", "--bare", seedDir, _bareRepoPath);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private async Task<Guid> CreateProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new { Name = $"Src {Guid.NewGuid():N}" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task InitializeSource_GitRepository_Valid_Returns200_PersistsGitRepositoryAndTransitionsStatus()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source", new { SourceType = "GitRepository", Location = _bareRepoPath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Analyzing", body.GetProperty("status").GetString());
        Assert.Equal(_bareRepoPath, body.GetProperty("gitRepository").GetString());
    }

    [Fact]
    public async Task InitializeSource_Empty_Returns200_TransitionsStatus_NoGitRepositoryPersisted()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/source", new { SourceType = "Empty" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Analyzing", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("gitRepository").ValueKind);
    }

    [Fact]
    public async Task InitializeSource_InvalidSourceType_Returns400()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source", new { SourceType = "NotASourceType" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitializeSource_GitRepository_MissingLocation_Returns400()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source", new { SourceType = "GitRepository" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitializeSource_GitRepository_NonexistentRepository_Returns503()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source",
            new { SourceType = "GitRepository", Location = Path.Combine(_tempRoot, "does-not-exist.git") });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task InitializeSource_GitRepository_InvalidBranch_Returns503()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source",
            new { SourceType = "GitRepository", Location = _bareRepoPath, Branch = "does-not-exist-branch" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task InitializeSource_UnknownProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{Guid.NewGuid()}/source", new { SourceType = "Empty" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task InitializeSource_Repeated_SecondCallReturns409()
    {
        var projectId = await CreateProjectAsync();
        var first = await _client.PostAsJsonAsync($"/api/projects/{projectId}/source", new { SourceType = "Empty" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await _client.PostAsJsonAsync($"/api/projects/{projectId}/source", new { SourceType = "Empty" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>Spec §11/§25 — a failed initialization must never falsely mark the Project as initialized/ready.</summary>
    [Fact]
    public async Task InitializeSource_FailedAttempt_ProjectStatusStaysCreated_AndCanBeRetried()
    {
        var projectId = await CreateProjectAsync();

        var failed = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source",
            new { SourceType = "GitRepository", Location = Path.Combine(_tempRoot, "does-not-exist.git") });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        var projectResponse = await _client.GetAsync($"/api/projects/{projectId}");
        var projectBody = await projectResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Created", projectBody.GetProperty("status").GetString());

        // A Project whose only prior attempt failed must still be
        // retryable — the failed attempt itself must not have consumed the
        // one-shot "Created -> Analyzing" transition.
        var retried = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/source", new { SourceType = "Empty" });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }
}
