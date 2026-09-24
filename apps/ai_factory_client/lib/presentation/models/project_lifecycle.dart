import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';

/// Where a track stage sits relative to the state the API reports **right
/// now**.
///
/// Deliberately not "completed" / "pending". `GET /api/projects/{id}` returns
/// one status and no history (gap `G-STAGE-TIMESTAMPS`; there is no
/// `/history` endpoint), so the only defensible statement about another stage
/// is where it lies on the track relative to the current one. A stage before
/// the current one was necessarily entered — the backend transition table
/// admits no way past it — but *when* is unknown, and a stage after the
/// current one may still have been visited already, because
/// `HumanReview -> Implementation` is a legal rework branch.
enum LifecyclePosition {
  /// Earlier on the track than the reported state.
  earlier,

  /// Exactly the state the API reported.
  current,

  /// The stage the reported off-track state is attached to — only
  /// `Repairing`, which the transition table reaches from `Testing` and
  /// returns to `Testing`.
  anchor,

  /// Later on the track than the reported state.
  later,

  /// The reported state is not on the track and names no stage, so this
  /// stage's relationship to it is unknown. `Paused`, `Failed`, `Cancelled`
  /// and an unrecognized value all overwrite `status`, and the stage they
  /// overwrote is not exposed by the API.
  undetermined,
}

/// A state the API reports that is not a stage on the main track.
enum OffTrackState {
  /// `Repairing` — the `Testing <-> Repairing` loop.
  repairing,

  /// `Paused` — a side state; the stage it was paused in is not exposed.
  paused,

  /// `Failed` — terminal; the stage it stopped at is not exposed.
  failed,

  /// `Cancelled` — terminal; the stage it stopped at is not exposed.
  cancelled,

  /// A value this client's [ProjectStatus] mirror does not know.
  unrecognized,
}

/// One rendered node of the lifecycle track.
class LifecycleNode {
  const LifecycleNode({
    required this.status,
    required this.position,
    required this.index,
  });

  final ProjectStatus status;
  final LifecyclePosition position;

  /// Zero-based position on [ProjectLifecycle.track], for the "step n of m"
  /// screen-reader label.
  final int index;

  bool get isCurrent => position == LifecyclePosition.current;
}

/// The lifecycle of one project, derived from the single status the API
/// reports — and from nothing else.
///
/// Pure and synchronous: no repository, no clock, no localization. The widget
/// renders it; `EnumLabels` names the stages.
///
/// The track is the backend's own forward chain, read out of
/// `AI.Factory.Domain.Entities.ProjectTransitions`:
///
///     Created -> Analyzing -> Specification -> Architecture -> Planning ->
///     Implementation -> Testing -> Reviewing -> HumanReview -> Completed
///
/// with `Testing <-> Repairing` as a loop, `Paused` as a side state, and
/// `Failed` / `Cancelled` as terminators. `Completed` is reachable only from
/// `HumanReview`, and only through approval.
class ProjectLifecycle {
  const ProjectLifecycle._({
    required this.nodes,
    required this.status,
    required this.rawStatus,
    required this.offTrack,
    required this.isStarted,
  });

  /// The forward chain, in backend order. `Repairing`, `Paused`, `Failed` and
  /// `Cancelled` are deliberately absent: none of them is a step forward.
  static const List<ProjectStatus> track = <ProjectStatus>[
    ProjectStatus.created,
    ProjectStatus.analyzing,
    ProjectStatus.specification,
    ProjectStatus.architecture,
    ProjectStatus.planning,
    ProjectStatus.implementation,
    ProjectStatus.testing,
    ProjectStatus.reviewing,
    ProjectStatus.humanReview,
    ProjectStatus.completed,
  ];

  factory ProjectLifecycle.of(Project project) {
    final status = project.status;
    final offTrack = _offTrackOf(status);

    // `Repairing` is the one off-track state the transition table pins to a
    // stage: it is entered only from `Testing` and leaves only to `Testing`.
    final anchorIndex = offTrack == OffTrackState.repairing
        ? track.indexOf(ProjectStatus.testing)
        : -1;
    final currentIndex = offTrack == null ? track.indexOf(status) : -1;

    final nodes = <LifecycleNode>[
      for (var i = 0; i < track.length; i++)
        LifecycleNode(
          status: track[i],
          index: i,
          position: _positionAt(
            i,
            currentIndex: currentIndex,
            anchorIndex: anchorIndex,
          ),
        ),
    ];

    return ProjectLifecycle._(
      nodes: List<LifecycleNode>.unmodifiable(nodes),
      status: status,
      rawStatus: project.rawStatus,
      offTrack: offTrack,
      isStarted: project.isStarted,
    );
  }

  final List<LifecycleNode> nodes;

  /// The state the API reported, unchanged.
  final ProjectStatus status;

  /// The wire value behind [status], shown when the client could not parse it.
  final String rawStatus;

  /// Null when [status] is a stage on [track].
  final OffTrackState? offTrack;

  final bool isStarted;

  /// True when no node carries [LifecyclePosition.current] — either because
  /// the reported state is off the track, or because it is unrecognized.
  bool get hasNoCurrentStage => offTrack != null;

  /// The node the API's status names, or null for an off-track state.
  LifecycleNode? get currentNode {
    for (final node in nodes) {
      if (node.isCurrent) return node;
    }
    return null;
  }

  /// The stage a `Repairing` project returns to, or null.
  LifecycleNode? get anchorNode {
    for (final node in nodes) {
      if (node.position == LifecyclePosition.anchor) return node;
    }
    return null;
  }

  /// Whether the track position is known to have stopped advancing.
  bool get isTerminal => status.isTerminal;

  static OffTrackState? _offTrackOf(ProjectStatus status) => switch (status) {
    ProjectStatus.repairing => OffTrackState.repairing,
    ProjectStatus.paused => OffTrackState.paused,
    ProjectStatus.failed => OffTrackState.failed,
    ProjectStatus.cancelled => OffTrackState.cancelled,
    ProjectStatus.unknown => OffTrackState.unrecognized,
    _ => null,
  };

  static LifecyclePosition _positionAt(
    int index, {
    required int currentIndex,
    required int anchorIndex,
  }) {
    if (anchorIndex >= 0) {
      if (index == anchorIndex) return LifecyclePosition.anchor;
      return index < anchorIndex
          ? LifecyclePosition.earlier
          : LifecyclePosition.later;
    }
    if (currentIndex < 0) return LifecyclePosition.undetermined;
    if (index == currentIndex) return LifecyclePosition.current;
    return index < currentIndex
        ? LifecyclePosition.earlier
        : LifecyclePosition.later;
  }
}
