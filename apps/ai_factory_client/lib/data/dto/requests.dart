/// Request bodies the API accepts.
///
/// Serialization is hand-written rather than generated: these are three or
/// four fields each, the wire shape must match the backend records exactly,
/// and omitting a null optional matters (`POST .../approve` accepts an empty
/// body, and sending `{"actor": null}` is not the same request as `{}`).
library;

import '../../domain/enums/backend_enums.dart';

/// `POST /api/projects` -> `CreateProjectRequest(Name, Description?)`.
///
/// Backend validation, mirrored client-side before the call: name required,
/// at most 200 characters, no `..` and no invalid filename characters;
/// description at most 2000 characters.
class CreateProjectRequest {
  const CreateProjectRequest({required this.name, this.description});

  final String name;
  final String? description;

  Map<String, dynamic> toJson() => <String, dynamic>{
    'name': name,
    if (description != null) 'description': description,
  };
}

/// `POST /api/projects/{id}/source` -> `InitializeProjectSourceRequest`.
///
/// `location` and `branch` apply to `GitRepository` only; the Factory always
/// owns the destination path, so nothing here is a filesystem target.
class InitializeProjectSourceRequest {
  const InitializeProjectSourceRequest({
    required this.sourceType,
    this.location,
    this.branch,
  });

  const InitializeProjectSourceRequest.empty()
    : sourceType = ProjectSourceType.empty,
      location = null,
      branch = null;

  const InitializeProjectSourceRequest.git({
    required String repositoryUrl,
    this.branch,
  }) : sourceType = ProjectSourceType.gitRepository,
       location = repositoryUrl;

  final ProjectSourceType sourceType;
  final String? location;
  final String? branch;

  Map<String, dynamic> toJson() => <String, dynamic>{
    'sourceType': sourceType.wireValue,
    if (location != null) 'location': location,
    if (branch != null) 'branch': branch,
  };
}

/// `POST /api/projects/{id}/start` and `POST /api/projects/{id}/approve`.
///
/// `actor` is an unauthenticated, caller-supplied label recorded for audit
/// purposes — the backend has no identity abstraction, so it is not a
/// verified claim and the client must not present it as one.
class ActorRequest {
  const ActorRequest({this.actor});

  final String? actor;

  Map<String, dynamic> toJson() => <String, dynamic>{
    if (actor != null) 'actor': actor,
  };
}

/// `PATCH /api/projects/{id}/status` and `PATCH /api/tasks/{id}/status`.
///
/// The project endpoint rejects `Completed` under every input: completion is
/// reachable only through the approval endpoint.
class UpdateStatusRequest {
  const UpdateStatusRequest(this.status);

  UpdateStatusRequest.projectStatus(ProjectStatus status)
    : status = status.wireValue;

  UpdateStatusRequest.workItemStatus(WorkItemStatus status)
    : status = status.wireValue;

  final String status;

  Map<String, dynamic> toJson() => <String, dynamic>{'status': status};
}

/// `POST /api/agents/runs` -> `CreateAgentRunRequest`.
class CreateAgentRunRequest {
  const CreateAgentRunRequest({
    required this.agentDefinitionId,
    this.projectId,
    this.input,
  });

  final String agentDefinitionId;
  final String? projectId;
  final String? input;

  Map<String, dynamic> toJson() => <String, dynamic>{
    'agentDefinitionId': agentDefinitionId,
    if (projectId != null) 'projectId': projectId,
    if (input != null) 'input': input,
  };
}

/// Query parameters for `GET /api/agents/runs`.
class AgentRunQuery {
  const AgentRunQuery({
    this.projectId,
    this.agentDefinitionId,
    this.status,
    this.from,
    this.to,
    this.page = 1,
    this.pageSize = 50,
  });

  final String? projectId;
  final String? agentDefinitionId;
  final AgentRunStatus? status;
  final DateTime? from;
  final DateTime? to;

  /// 1-based. The backend rejects anything below 1.
  final int page;

  /// The backend rejects anything below 1 or above 100.
  final int pageSize;

  Map<String, dynamic> toQueryParameters() => <String, dynamic>{
    if (projectId != null) 'projectId': projectId,
    if (agentDefinitionId != null) 'agentDefinitionId': agentDefinitionId,
    if (status != null && status != AgentRunStatus.unknown)
      'status': status!.wireValue,
    if (from != null) 'from': from!.toUtc().toIso8601String(),
    if (to != null) 'to': to!.toUtc().toIso8601String(),
    'page': page,
    'pageSize': pageSize,
  };
}
