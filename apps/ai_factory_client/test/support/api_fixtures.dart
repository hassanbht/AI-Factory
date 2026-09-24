import 'dart:convert';
import 'dart:io';

/// Loads the JSON payloads captured from a running AI Factory `v1.0.0` API.
///
/// These are recordings, not hand-written examples: every file under
/// `test/fixtures/api/` except [agentRunStructured] was produced by
/// `tool/api_fixtures.dart` calling the real endpoints on a throwaway
/// database, then normalized (stable ids and instants, the timestamp format
/// preserved). Tests that assert a shape assert the shape the backend
/// actually returns; `--check` against a fresh API is how drift is caught.
class ApiFixtures {
  const ApiFixtures._();

  static const String _directory = 'test/fixtures/api';

  static String raw(String name) =>
      File('$_directory/$name.json').readAsStringSync();

  static Map<String, dynamic> object(String name) =>
      jsonDecode(raw(name)) as Map<String, dynamic>;

  static List<Map<String, dynamic>> array(String name) =>
      (jsonDecode(raw(name)) as List<dynamic>)
          .cast<Map<String, dynamic>>()
          .toList(growable: false);

  // Names match the files on disk.
  static const String project = 'project';
  static const String projectsList = 'projects_list';
  static const String workItems = 'work_items';
  static const String projectPlan = 'project_plan';
  static const String agentRunsPage = 'agent_runs_page';
  static const String agentDefinitions = 'agent_definitions';
  static const String projectDiagnostics = 'project_diagnostics';
  static const String deployment = 'deployment';
  static const String database = 'database';
  static const String configuration = 'configuration';
  static const String schedulerStatus = 'scheduler_status';
  static const String agentMetrics = 'agent_metrics';
  static const String problemValidation = 'problem_validation';
  static const String problemConflict = 'problem_conflict';
  static const String auditLogPage = 'audit_log_page';
  static const String approvedProject = 'approved_project';

  /// Hand-authored: a structured result exists only after a real agent
  /// execution. Its fields are checked against a captured run by
  /// `tool/api_fixtures.dart --check`.
  static const String agentRunStructured = 'agent_run_structured';
}
