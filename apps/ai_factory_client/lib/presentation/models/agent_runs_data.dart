import 'package:equatable/equatable.dart';

import '../../domain/models/agent_run.dart';

/// What the AgentRuns section renders: a page of runs joined with the agent
/// definitions that name them.
///
/// `AgentRunDto` carries only `agentDefinitionId` (`docs/flutter-api-contract.md`
/// §6) — the agent's display name and type live on `AgentDefinition`, reached
/// through the separate `GET /api/agents/definitions` list. [definitionsById]
/// is that lookup, fetched once per load rather than once per run.
class AgentRunsData extends Equatable {
  const AgentRunsData({
    required this.runs,
    required this.definitionsById,
    required this.totalCount,
  });

  final List<AgentRun> runs;
  final Map<String, AgentDefinition> definitionsById;

  /// `AgentRunPageDto.totalCount` — may exceed `runs.length` when the project
  /// has more runs than the page size requested.
  final int totalCount;

  bool get isEmpty => runs.isEmpty;

  /// Null when the run's `agentDefinitionId` does not match any definition
  /// the API returned — a defensive case (a deleted or disabled definition),
  /// never assumed to be a client bug.
  AgentDefinition? definitionFor(AgentRun run) =>
      definitionsById[run.agentDefinitionId];

  @override
  List<Object?> get props => [runs, definitionsById, totalCount];
}
