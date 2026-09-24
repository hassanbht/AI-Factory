import '../../domain/enums/backend_enums.dart';
import '../../domain/models/project.dart';

/// What the API says about where a project's code comes from.
///
/// `GET /api/projects/{id}` exposes exactly two source fields,
/// `gitRepository` and `gitBranch`, and no `sourceType` — that enum is
/// request-only, on `POST /api/projects/{id}/source`. So the kind is derived
/// from the one observable fact: whether a repository was recorded.
///
/// A project still in `Created` has had no source decision made at all, which
/// is different from "an empty workspace was chosen", and [ProjectSourceView]
/// keeps the two apart rather than reporting the second for the first.
class ProjectSourceView {
  const ProjectSourceView._({required this.kind, this.repository, this.branch});

  factory ProjectSourceView.of(Project project) {
    if (!project.hasInitializedSource) {
      return const ProjectSourceView._(kind: ProjectSourceKind.notInitialized);
    }

    final repository = project.gitRepository;
    if (repository == null || repository.trim().isEmpty) {
      return const ProjectSourceView._(kind: ProjectSourceKind.empty);
    }

    final branch = project.gitBranch;
    return ProjectSourceView._(
      kind: ProjectSourceKind.gitRepository,
      repository: redactCredentials(repository.trim()),
      branch: (branch == null || branch.trim().isEmpty) ? null : branch.trim(),
    );
  }

  final ProjectSourceKind kind;

  /// The repository location with any embedded credentials removed — see
  /// [redactCredentials]. Null unless [kind] is
  /// [ProjectSourceKind.gitRepository].
  final String? repository;

  /// Null when the operator did not name one and the repository default was
  /// used; the API does not report which branch that resolved to.
  final String? branch;

  bool get isGit => kind == ProjectSourceKind.gitRepository;

  /// Strips the `user:token@` userinfo from a repository URL.
  ///
  /// An operator can paste `https://x-access-token:ghp_…@github.com/o/r.git`
  /// into the create wizard, and the backend stores the string it was given.
  /// The console must not then put that token on screen, into a screenshot or
  /// into a bug report, so the userinfo is removed before the value is ever
  /// rendered. Everything else — host, path, `git@host:org/repo.git` SSH
  /// syntax, a local path — is operator data and is shown verbatim.
  static String redactCredentials(String location) {
    // Only the URL form carries userinfo. `git@host:org/repo.git` is an SSH
    // user, not a secret, and scp-style syntax has no password field.
    final match = RegExp(r'^([a-zA-Z][a-zA-Z0-9+.-]*://)([^/@]+@)(.*)$')
        .firstMatch(location);
    if (match == null) return location;

    final userinfo = match.group(2)!;
    // A bare `user@host` with no password is a username, which is not a
    // credential and is useful context; anything with a `:` is dropped whole,
    // because the part before the colon is often the token's label.
    if (!userinfo.contains(':')) return location;

    return '${match.group(1)}${match.group(3)}';
  }
}

/// The source concepts the current API lets the client distinguish.
enum ProjectSourceKind {
  /// No source decision has been made yet: the project is still `Created`.
  notInitialized,

  /// A source was initialized and no repository was recorded, which is what
  /// [ProjectSourceType.empty] produces.
  empty,

  /// A repository was recorded.
  gitRepository,
}
