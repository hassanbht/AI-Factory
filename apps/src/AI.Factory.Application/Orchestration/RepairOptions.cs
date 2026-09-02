using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

public sealed class RepairOptions
{
    public RepairExhaustionPolicy Policy { get; set; } = RepairExhaustionPolicy.Failed;
}
