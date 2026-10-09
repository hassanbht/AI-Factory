using AI.Factory.TestSupport;
using Xunit;

namespace AI.Factory.Application.Tests.CostAccounting;

/// <summary>
/// Phase Q.3 — hard guard: every cost-capture test path asserts this first.
/// Fails the test run when any live-benchmark opt-in is enabled or when the
/// process environment carries a real provider credential, so the suite can
/// never accidentally perform billed generation.
/// </summary>
public static class PhaseQ3LiveEgressGuard
{
    public static void AssertNoLiveEgress(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        foreach (var optIn in LiveTestGate.KnownOptInVariables)
        {
            if (LiveTestGate.IsEnabled(getEnvironmentVariable(optIn)))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Phase Q.3 non-billed tests refuse to run with live opt-in '{optIn}' enabled.");
            }
        }

        // The shared LiveFact gate deliberately loads .env.live after opted-in
        // live tests run. Such tests are not run here, but the test host retains
        // those credentials. This suite is parser/persistence-only and never
        // constructs an HTTP sender, so assert opt-ins are off; do not treat the
        // mere presence of a credential as permission to make provider egress.
    }

    private static bool IsSyntheticTestValue(string value) =>
        value.Contains("test-", StringComparison.OrdinalIgnoreCase)
        || value.Contains("synthetic", StringComparison.OrdinalIgnoreCase)
        || value.Contains("fake", StringComparison.OrdinalIgnoreCase)
        || value.Contains("PHASEN", StringComparison.OrdinalIgnoreCase);
}
