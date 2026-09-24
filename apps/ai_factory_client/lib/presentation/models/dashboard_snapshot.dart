import 'package:equatable/equatable.dart';

import '../../domain/enums/backend_enums.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import '../../domain/models/project.dart';

/// Project counts by lifecycle position.
///
/// Computed client-side from `GET /api/projects`, which returns **every**
/// project with no pagination (gap `G-PROJECT-PAGING`). The counts are
/// therefore exact for the current response rather than an estimate — but
/// they are an aggregation this client performs, not a figure the backend
/// reports, and the Dashboard says so.
class ProjectStatusCounts extends Equatable {
  const ProjectStatusCounts({
    required this.total,
    required this.active,
    required this.humanReview,
    required this.paused,
    required this.completed,
    required this.failed,
    required this.cancelled,
    required this.notStarted,
  });

  factory ProjectStatusCounts.from(Iterable<Project> projects) {
    var total = 0;
    var active = 0;
    var humanReview = 0;
    var paused = 0;
    var completed = 0;
    var failed = 0;
    var cancelled = 0;
    var notStarted = 0;

    for (final project in projects) {
      total++;
      switch (project.status) {
        case ProjectStatus.humanReview:
          humanReview++;
        case ProjectStatus.paused:
          paused++;
        case ProjectStatus.completed:
          completed++;
        case ProjectStatus.failed:
          failed++;
        case ProjectStatus.cancelled:
          cancelled++;
        case ProjectStatus.created:
          notStarted++;
        // `unknown` is counted in the total only: a state this client cannot
        // interpret must not be silently folded into one it can.
        default:
          if (project.status.isActive) active++;
      }
    }

    return ProjectStatusCounts(
      total: total,
      active: active,
      humanReview: humanReview,
      paused: paused,
      completed: completed,
      failed: failed,
      cancelled: cancelled,
      notStarted: notStarted,
    );
  }

  static const ProjectStatusCounts empty = ProjectStatusCounts(
    total: 0,
    active: 0,
    humanReview: 0,
    paused: 0,
    completed: 0,
    failed: 0,
    cancelled: 0,
    notStarted: 0,
  );

  final int total;

  /// `Analyzing` … `Reviewing`: the Factory is advancing the project.
  final int active;

  final int humanReview;
  final int paused;
  final int completed;
  final int failed;
  final int cancelled;

  /// `Created` — source not initialized, so the worker will never pick it up.
  final int notStarted;

  @override
  List<Object?> get props => [
    total,
    active,
    humanReview,
    paused,
    completed,
    failed,
    cancelled,
    notStarted,
  ];
}

/// Everything the Dashboard renders, assembled from the endpoints that are
/// reachable without an operational key, plus the two that are not.
class DashboardSnapshot extends Equatable {
  const DashboardSnapshot({
    required this.counts,
    required this.needsAttention,
    required this.recent,
    this.health,
    this.scheduler,
    this.agentMetrics,
    this.agentMetricsRequiresKey = false,
  });

  /// How many projects each band shows before it stops.
  static const int bandLimit = 5;

  final ProjectStatusCounts counts;

  /// Projects awaiting a human decision, then failed ones, then any carrying
  /// a `currentError`. Never inferred — every entry comes from a field the
  /// API actually returned.
  final List<Project> needsAttention;

  /// Most recently updated projects, newest first.
  final List<Project> recent;

  /// Null when `/health/live` and `/health/ready` could not be read at all.
  final SystemHealth? health;

  final SchedulerStatus? scheduler;

  /// `GET /api/agents/metrics` is behind the operational key. Null means it
  /// was not obtained, and [agentMetricsRequiresKey] distinguishes "the
  /// backend demanded a key" from "the call failed".
  final AgentMetrics? agentMetrics;

  final bool agentMetricsRequiresKey;

  bool get hasProjects => counts.total > 0;

  @override
  List<Object?> get props => [
    counts,
    needsAttention,
    recent,
    health,
    scheduler,
    agentMetrics,
    agentMetricsRequiresKey,
  ];
}
