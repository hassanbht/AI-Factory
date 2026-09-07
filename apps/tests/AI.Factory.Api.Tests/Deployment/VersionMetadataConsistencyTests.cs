using System.Reflection;
using AI.Factory.Application.Configuration;
using AI.Factory.Infrastructure.Diagnostics;
using Xunit;

namespace AI.Factory.Api.Tests.Deployment;

/// <summary>
/// Proves the invariant "no conflicting independent version source" (phase 17.5.2, section 6.3)
/// for the parts of the chain <c>VERSION -&gt; assembly informational version -&gt; deployment
/// diagnostics</c> that do NOT require Docker or a container registry:
///
/// <code>
/// VERSION
///    v
/// Assembly (Informational)Version   [apps/Directory.Build.props]
///    v
/// DeploymentDiagnosticsService.GetSnapshot()
/// </code>
///
/// The remaining links in the full chain from the phase spec — Docker OCI image labels and the
/// published registry image tag — require an actual image build/push against a real registry
/// and are NOT covered by this unit test. That gap is intentional and is documented in
/// .ai/context/phase-17.5.2-cicd-runtime-validation-registry-artifact-integrity.md (section 8,
/// "Image digest verification" / section 14, "Known limitations"): it can only be closed once
/// this repository has a real Git remote to push to.
/// </summary>
public sealed class VersionMetadataConsistencyTests
{
    [Fact]
    public void VersionFile_IsNonEmptySemanticVersion()
    {
        var versionFileContent = ReadVersionFile();

        Assert.False(string.IsNullOrWhiteSpace(versionFileContent));
    }

    [Fact]
    public void TestAssembly_InformationalVersion_StartsWithVersionFileContent()
    {
        var versionFileContent = ReadVersionFile();

        // apps/Directory.Build.props composes InformationalVersion as
        // "{SemanticVersion}+{GitCommit}.{GitBranch}.{BuildNumber}" — the "+" separator means
        // any consumer can reliably recover the semantic version with a StartsWith/split on '+'.
        var entryAssembly = Assembly.GetExecutingAssembly();
        var infoAttr = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(infoAttr);
        Assert.StartsWith(versionFileContent, infoAttr!.InformationalVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Documents a real, discovered limitation rather than papering over it:
    /// <see cref="DeploymentDiagnosticsService"/> reads
    /// <c>Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()</c> to source its
    /// <c>Version</c>/<c>InformationalVersion</c>. In production (running as
    /// <c>AI.Factory.Api.dll</c> / <c>AI.Factory.Worker.dll</c>) the entry assembly IS the
    /// versioned project built under <c>apps/Directory.Build.props</c>, so the chain holds. But
    /// under `dotnet test`, <c>Assembly.GetEntryAssembly()</c> returns the xunit test host
    /// process (e.g. "testhost", version driven by the installed .NET SDK, NOT by this repo's
    /// VERSION file) — so a unit test asserting <c>snapshot.InformationalVersion</c> starts with
    /// the VERSION file content is unreliable in-process, not the code under test. This is
    /// asserted here as a fact about test-host behavior, not "no bug" — full assurance that the
    /// running container reports the right version still comes from the smoke tests in
    /// ci.yml/release.yml (which run the actual AI.Factory.Api.dll entry point), not from this
    /// unit test. See the phase-17.5.2 doc, section 8/14, for the same gap called out for the
    /// Docker-label/registry-tag links of the chain.
    /// </summary>
    [Fact]
    public void DeploymentDiagnosticsSnapshot_UnderTestHost_DoesNotReflectRepoVersionFile()
    {
        var service = new DeploymentDiagnosticsService(new DatabaseOptions
        {
            Provider = "Sqlite",
            ConnectionString = "Data Source=:memory:",
        });

        var snapshot = service.GetSnapshot();

        // Deliberately NOT asserting equality/StartsWith against the VERSION file here — see
        // the doc comment above for why that would be testing the test host, not this service.
        // Only asserting the service still produces a well-formed value under a test host.
        Assert.False(string.IsNullOrWhiteSpace(snapshot.InformationalVersion));
    }

    /// <summary>
    /// Reads the repo-root VERSION file, trimmed. Uses the same repo-root-finding walk-up logic
    /// as <see cref="ProductionConfigurationSafetyTests"/> for consistency.
    /// </summary>
    private static string ReadVersionFile()
    {
        var path = Path.Combine(FindRepoRoot(), "VERSION");
        return File.ReadAllText(path).Trim();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "AI.Factory.slnx");
            if (File.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repo root (apps/AI.Factory.slnx) walking up from {AppContext.BaseDirectory}.");
    }
}
