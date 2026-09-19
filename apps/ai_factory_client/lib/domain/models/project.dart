import 'package:equatable/equatable.dart';

import '../enums/backend_enums.dart';

/// A Factory project.
///
/// Immutable, no JSON, no `Rx` — the presentation layer never sees the DTO
/// this was mapped from.
class Project extends Equatable {
  const Project({
    required this.id,
    required this.name,
    required this.slug,
    required this.status,
    required this.rawStatus,
    required this.createdAtUtc,
    required this.updatedAtUtc,
    this.description,
    this.gitRepository,
    this.gitBranch,
    this.lifecycleStartedAtUtc,
    this.currentError,
  });

  final String id;
  final String name;
  final String slug;
  final String? description;

  final ProjectStatus status;

  /// The exact wire value, kept for diagnostics and for rendering a state a
  /// newer backend introduced (`status` would be `unknown` in that case).
  final String rawStatus;

  final String? gitRepository;
  final String? gitBranch;

  /// Set once the project has been started. Null means the project is
  /// operator-driven: the worker will never advance it on its own.
  final DateTime? lifecycleStartedAtUtc;

  final String? currentError;
  final DateTime createdAtUtc;
  final DateTime updatedAtUtc;

  bool get isStarted => lifecycleStartedAtUtc != null;

  /// The source step has been completed; `Created` means it has not.
  bool get hasInitializedSource => status != ProjectStatus.created;

  /// The backend accepts an approval only in this state.
  bool get awaitsHumanApproval => status == ProjectStatus.humanReview;

  bool get hasError => currentError != null && currentError!.isNotEmpty;

  @override
  List<Object?> get props => [
    id,
    name,
    slug,
    description,
    status,
    rawStatus,
    gitRepository,
    gitBranch,
    lifecycleStartedAtUtc,
    currentError,
    createdAtUtc,
    updatedAtUtc,
  ];
}
