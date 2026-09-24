import 'package:equatable/equatable.dart';

import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';

/// The status groups the Projects screen filters by.
///
/// A group, not a single status, because the operator thinks in terms of
/// "what is running" rather than in terms of nine individual lifecycle
/// stages. Each group maps onto real backend statuses — nothing here invents
/// a state the API does not have.
enum ProjectStatusFilter {
  all,
  notStarted,
  active,
  humanReview,
  paused,
  completed,
  failed;

  String get labelKey => 'projects.filter.$name';

  bool matches(Project project) => switch (this) {
    ProjectStatusFilter.all => true,
    ProjectStatusFilter.notStarted => project.status == ProjectStatus.created,
    ProjectStatusFilter.active => project.status.isActive,
    ProjectStatusFilter.humanReview =>
      project.status == ProjectStatus.humanReview,
    ProjectStatusFilter.paused => project.status == ProjectStatus.paused,
    ProjectStatusFilter.completed => project.status == ProjectStatus.completed,
    // Cancelled sits here too: both are "this project is not going to finish
    // on its own", which is the question the operator is asking.
    ProjectStatusFilter.failed =>
      project.status == ProjectStatus.failed ||
          project.status == ProjectStatus.cancelled,
  };
}

/// Status group plus a free-text query. Applied client-side, because
/// `GET /api/projects` accepts no query parameters at all.
class ProjectListFilter extends Equatable {
  const ProjectListFilter({
    this.status = ProjectStatusFilter.all,
    this.query = '',
  });

  static const ProjectListFilter none = ProjectListFilter();

  final ProjectStatusFilter status;
  final String query;

  bool get isActive =>
      status != ProjectStatusFilter.all || query.trim().isNotEmpty;

  ProjectListFilter copyWith({ProjectStatusFilter? status, String? query}) =>
      ProjectListFilter(
        status: status ?? this.status,
        query: query ?? this.query,
      );

  /// Matches the project name and the slug. The id is deliberately excluded:
  /// a partial GUID match produces surprising results and the operator copies
  /// ids whole.
  bool matches(Project project) {
    if (!status.matches(project)) return false;

    final needle = query.trim().toLowerCase();
    if (needle.isEmpty) return true;

    return project.name.toLowerCase().contains(needle) ||
        project.slug.toLowerCase().contains(needle);
  }

  @override
  List<Object?> get props => [status, query];
}

/// Presentation ordering for the project list.
///
/// This is a **UI organization rule, not a business priority**: the backend
/// exposes no ordering parameter and no notion of project importance, so the
/// list is sorted client-side on the already-retrieved collection. Change
/// [rank] to change the grouping; nothing else depends on it.
class ProjectSort {
  const ProjectSort._();

  /// Lower sorts first.
  static int rank(Project project) => switch (project.status) {
    ProjectStatus.humanReview => 1,
    ProjectStatus.completed => 2,
    ProjectStatus.failed || ProjectStatus.cancelled => 3,
    ProjectStatus.unknown => 4,
    // Everything still in motion, plus `Created` and `Paused`, which are the
    // ones an operator is most likely to act on next.
    _ => 0,
  };

  static List<Project> apply(Iterable<Project> projects) {
    final sorted = projects.toList();
    sorted.sort((a, b) {
      final byRank = rank(a).compareTo(rank(b));
      if (byRank != 0) return byRank;
      return b.updatedAtUtc.compareTo(a.updatedAtUtc);
    });
    return List<Project>.unmodifiable(sorted);
  }

  /// Dashboard band: projects that need a person, in the order a person
  /// should look at them. Human review first because it blocks completion,
  /// then outright failures, then projects still running but carrying an
  /// error the backend recorded.
  static List<Project> needsAttention(Iterable<Project> projects, {int? take}) {
    int? attentionRank(Project project) {
      if (project.status == ProjectStatus.humanReview) return 0;
      if (project.status == ProjectStatus.failed) return 1;
      if (project.hasError) return 2;
      return null;
    }

    final ranked =
        <(int, Project)>[
          for (final project in projects)
            if (attentionRank(project) case final int r) (r, project),
        ]..sort((a, b) {
          final byRank = a.$1.compareTo(b.$1);
          if (byRank != 0) return byRank;
          return b.$2.updatedAtUtc.compareTo(a.$2.updatedAtUtc);
        });

    final result = ranked.map((e) => e.$2).toList();
    return List<Project>.unmodifiable(
      take == null || result.length <= take ? result : result.sublist(0, take),
    );
  }

  static List<Project> mostRecent(Iterable<Project> projects, {int? take}) {
    final sorted = projects.toList()
      ..sort((a, b) => b.updatedAtUtc.compareTo(a.updatedAtUtc));
    return List<Project>.unmodifiable(
      take == null || sorted.length <= take ? sorted : sorted.sublist(0, take),
    );
  }
}
