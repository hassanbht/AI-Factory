namespace AI.Factory.Application.Resilience;

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public const string AgentExecution = "AgentExecution";
    public const string ProviderCall = "ProviderCall";
    public const string BuildExecution = "BuildExecution";
    public const string TestExecution = "TestExecution";
    public const string RepairExecution = "RepairExecution";
    public const string BackgroundOperation = "BackgroundOperation";

    public Dictionary<string, ResilienceProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ResilienceProfile GetProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CreateDefaultProfile("Default");
        }

        if (Profiles.TryGetValue(name, out var profile))
        {
            return profile;
        }

        return CreateDefaultProfile(name);
    }

    public static ResilienceProfile CreateDefaultProfile(string name)
    {
        return name.Trim() switch
        {
            AgentExecution => new ResilienceProfile
            {
                Name = AgentExecution,
                Enabled = true,
                MaxRetryAttempts = 2,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(15),
                BackoffType = BackoffType.Exponential,
                UseJitter = true,
                AttemptTimeout = TimeSpan.FromMinutes(5),
                OperationTimeout = TimeSpan.FromMinutes(15),
                RetryTransientFailuresOnly = true
            },
            ProviderCall => new ResilienceProfile
            {
                Name = ProviderCall,
                Enabled = true,
                MaxRetryAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(10),
                BackoffType = BackoffType.Exponential,
                UseJitter = true,
                AttemptTimeout = TimeSpan.FromMinutes(1),
                OperationTimeout = TimeSpan.FromMinutes(5),
                RetryTransientFailuresOnly = true
            },
            BuildExecution => new ResilienceProfile
            {
                Name = BuildExecution,
                Enabled = true,
                MaxRetryAttempts = 0, // Compiler errors are deterministic; do not retry by default
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = BackoffType.Constant,
                UseJitter = false,
                AttemptTimeout = TimeSpan.FromMinutes(3),
                OperationTimeout = TimeSpan.FromMinutes(5),
                RetryTransientFailuresOnly = true
            },
            TestExecution => new ResilienceProfile
            {
                Name = TestExecution,
                Enabled = true,
                MaxRetryAttempts = 0, // Test failures are deterministic; do not retry by default
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(5),
                BackoffType = BackoffType.Constant,
                UseJitter = false,
                AttemptTimeout = TimeSpan.FromMinutes(5),
                OperationTimeout = TimeSpan.FromMinutes(10),
                RetryTransientFailuresOnly = true
            },
            RepairExecution => new ResilienceProfile
            {
                Name = RepairExecution,
                Enabled = true,
                MaxRetryAttempts = 1, // At most 1 immediate retry for transient infra/process failure during repair step
                InitialDelay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromSeconds(10),
                BackoffType = BackoffType.Exponential,
                UseJitter = true,
                AttemptTimeout = TimeSpan.FromMinutes(5),
                OperationTimeout = TimeSpan.FromMinutes(15),
                RetryTransientFailuresOnly = true
            },
            BackgroundOperation => new ResilienceProfile
            {
                Name = BackgroundOperation,
                Enabled = true,
                MaxRetryAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(30),
                BackoffType = BackoffType.Exponential,
                UseJitter = true,
                AttemptTimeout = TimeSpan.FromMinutes(2),
                OperationTimeout = TimeSpan.FromMinutes(10),
                RetryTransientFailuresOnly = true
            },
            _ => new ResilienceProfile
            {
                Name = name,
                Enabled = true,
                MaxRetryAttempts = 2,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(10),
                BackoffType = BackoffType.Exponential,
                UseJitter = true,
                AttemptTimeout = TimeSpan.FromMinutes(2),
                OperationTimeout = TimeSpan.FromMinutes(5),
                RetryTransientFailuresOnly = true
            }
        };
    }

    public void Validate()
    {
        foreach (var (key, profile) in Profiles)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidOperationException("Resilience profile key cannot be null or empty.");
            }

            if (profile is null)
            {
                throw new InvalidOperationException($"Resilience profile '{key}' cannot be null.");
            }

            if (profile.MaxRetryAttempts < 0)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' has invalid MaxRetryAttempts: {profile.MaxRetryAttempts}. Must be >= 0.");
            }

            if (profile.InitialDelay < TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' has negative InitialDelay: {profile.InitialDelay}.");
            }

            if (profile.MaxDelay < profile.InitialDelay)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' has MaxDelay ({profile.MaxDelay}) smaller than InitialDelay ({profile.InitialDelay}).");
            }

            if (profile.AttemptTimeout.HasValue && profile.AttemptTimeout.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' has non-positive AttemptTimeout: {profile.AttemptTimeout}.");
            }

            if (profile.OperationTimeout.HasValue && profile.OperationTimeout.Value <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' has non-positive OperationTimeout: {profile.OperationTimeout}.");
            }

            if (profile.AttemptTimeout.HasValue && profile.OperationTimeout.HasValue && profile.AttemptTimeout.Value > profile.OperationTimeout.Value)
            {
                throw new InvalidOperationException(
                    $"Resilience profile '{key}' AttemptTimeout ({profile.AttemptTimeout}) cannot exceed OperationTimeout ({profile.OperationTimeout}).");
            }
        }
    }
}
