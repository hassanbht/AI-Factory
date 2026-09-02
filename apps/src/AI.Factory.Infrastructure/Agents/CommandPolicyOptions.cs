namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Enforced once, centrally, in SystemProcessRunner — every process launch
/// in the app goes through it, so no caller can bypass the policy. Deny
/// always wins over allow, and an empty AllowedCommands list means
/// "nothing is allowed" (explicit allow-list, not "allow everything").
/// </summary>
public sealed class CommandPolicyOptions
{
    public string[] AllowedCommands { get; set; } = { "docker", "dotnet" };

    public string[] DeniedCommands { get; set; } =
    {
        "rm", "del", "rmdir", "format", "shutdown", "reboot",
        "curl", "wget", "powershell", "pwsh", "cmd", "bash", "sh", "ssh", "scp"
    };
}
