import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import '../../core/errors/app_error.dart';
import '../../core/state/view_state.dart';
import '../../core/theme/app_spacing.dart';
import '../../domain/models/agent_run.dart';
import '../../domain/models/operations.dart';
import 'panels.dart';
import 'timestamp_text.dart';

/// One Operations section, with the whole state matrix in one place.
///
/// Operations is several independent endpoints, so each section renders its
/// own Loading / Refreshing / Success / Empty / Unauthorized / Failure state
/// and offers its own retry (Workstream Y and Z). A section that fails leaves
/// every other section on screen, and a section that has data keeps showing
/// it while a refresh is in flight.
///
/// `401` is deliberately **not** rendered as a server failure: it means the
/// endpoint is behind the operational key, which is an operator action, not
/// an outage.
class OperationsSection<T> extends StatelessWidget {
  const OperationsSection({
    required this.title,
    required this.state,
    required this.builder,
    this.subtitle,
    this.onRetry,
    this.onEnterKey,
    super.key,
  });

  final String title;
  final String? subtitle;
  final ViewState<T> state;
  final Widget Function(BuildContext context, T data) builder;
  final VoidCallback? onRetry;

  /// Opens the operator-access entry. Null hides the action.
  final VoidCallback? onEnterKey;

  @override
  Widget build(BuildContext context) {
    return SectionCard(
      title: title,
      subtitle: subtitle,
      child: state.map(
        idle: () => const _SectionProgress(),
        loading: () => const _SectionProgress(),
        refreshing: (data) => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const LinearProgressIndicator(minHeight: 2),
            const SizedBox(height: AppSpacing.sm),
            builder(context, data),
          ],
        ),
        success: (data) => builder(context, data),
        partial: (data, _) => builder(context, data),
        empty: () => Text(
          'operations.section.empty'.tr(),
          style: Theme.of(context).textTheme.bodySmall
              ?.copyWith(color: Theme.of(context).colorScheme.onSurfaceVariant),
        ),
        failure: (error, stale) => _SectionFailure(
          error: error,
          stale: stale == null ? null : builder(context, stale),
          onRetry: onRetry,
          onEnterKey: onEnterKey,
        ),
      ),
    );
  }
}

class _SectionProgress extends StatelessWidget {
  const _SectionProgress();

  @override
  Widget build(BuildContext context) => const Padding(
    padding: EdgeInsets.symmetric(vertical: AppSpacing.sm),
    child: LinearProgressIndicator(minHeight: 2),
  );
}

class _SectionFailure extends StatelessWidget {
  const _SectionFailure({
    required this.error,
    required this.stale,
    required this.onRetry,
    required this.onEnterKey,
  });

  final AppError error;
  final Widget? stale;
  final VoidCallback? onRetry;
  final VoidCallback? onEnterKey;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final isUnauthorized = error is AuthenticationError;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Semantics(
          liveRegion: true,
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Icon(
                isUnauthorized ? Icons.key_outlined : Icons.error_outline,
                size: 18,
                color: isUnauthorized
                    ? theme.colorScheme.onSurfaceVariant
                    : theme.colorScheme.error,
              ),
              const SizedBox(width: AppSpacing.sm),
              Expanded(
                child: Text(
                  isUnauthorized
                      ? 'operatorAccess.required'.tr()
                      : 'operations.section.failed'.tr(),
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: isUnauthorized
                        ? theme.colorScheme.onSurfaceVariant
                        : theme.colorScheme.error,
                  ),
                ),
              ),
            ],
          ),
        ),
        const SizedBox(height: AppSpacing.sm),
        Wrap(
          spacing: AppSpacing.sm,
          children: [
            if (isUnauthorized && onEnterKey != null)
              FilledButton.tonalIcon(
                onPressed: onEnterKey,
                icon: const Icon(Icons.key_outlined, size: 16),
                label: Text('operatorAccess.enterKey'.tr()),
              ),
            if (onRetry != null)
              TextButton(onPressed: onRetry, child: Text('common.retry'.tr())),
          ],
        ),
        if (stale != null) ...[
          const SizedBox(height: AppSpacing.sm),
          Text(
            'operations.section.stale'.tr(),
            style: theme.textTheme.labelSmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: AppSpacing.xs),
          Opacity(opacity: 0.6, child: stale!),
        ],
      ],
    );
  }
}

/// Health as a word plus an icon, never as a colour alone (Workstream AC).
class HealthIndicator extends StatelessWidget {
  const HealthIndicator({
    required this.label,
    required this.healthy,
    this.degraded = false,
    super.key,
  });

  final String label;
  final bool healthy;

  /// The probe answered, and the answer was "not healthy" — different from
  /// "the probe could not be reached at all", which is unknown, not negative.
  final bool degraded;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    final (text, icon, color) = healthy
        ? (
            'operations.health.healthy'.tr(),
            Icons.check_circle_outline,
            theme.colorScheme.primary,
          )
        : degraded
        ? (
            'operations.health.unhealthy'.tr(),
            Icons.warning_amber_outlined,
            theme.colorScheme.error,
          )
        : (
            'operations.health.unavailable'.tr(),
            Icons.help_outline,
            theme.colorScheme.onSurfaceVariant,
          );

    return Semantics(
      label: '$label: $text',
      excludeSemantics: true,
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 16, color: color),
          const SizedBox(width: AppSpacing.xs),
          Text(text, style: theme.textTheme.bodyMedium),
        ],
      ),
    );
  }
}

/// API liveness and readiness, and nothing beyond them.
///
/// `/health/live` and `/health/ready` answer `text/plain` with a single word,
/// so there is no per-check breakdown to show (`G-HEALTH-DETAIL`) and no
/// worker health at all (`G-WORKER-HEALTH`). The section says "API", never
/// "System", because only the API answered.
class SystemHealthPanel extends StatelessWidget {
  const SystemHealthPanel({required this.health, super.key});

  final SystemHealth health;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FactList(
          facts: <Fact>[
            Fact(
              label: 'operations.health.live'.tr(),
              child: HealthIndicator(
                label: 'operations.health.live'.tr(),
                healthy: health.isAlive,
                degraded: !health.isAlive,
              ),
            ),
            Fact(
              label: 'operations.health.ready'.tr(),
              child: HealthIndicator(
                label: 'operations.health.ready'.tr(),
                healthy: health.isReady,
                degraded: health.isDegraded,
              ),
            ),
            Fact(
              label: 'operations.health.checkedAt'.tr(),
              child: TimestampText(timestamp: health.checkedAtUtc),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.md),
        UnavailableNote(
          title: 'operations.health.detail'.tr(),
          reason: 'operations.health.detailUnavailable'.tr(),
        ),
        const SizedBox(height: AppSpacing.sm),
        UnavailableNote(
          title: 'operations.health.worker'.tr(),
          reason: 'operations.health.workerUnavailable'.tr(),
        ),
      ],
    );
  }
}

/// Version and runtime metadata, exactly the fields the endpoint returns.
class DeploymentInfoPanel extends StatelessWidget {
  const DeploymentInfoPanel({required this.deployment, super.key});

  final DeploymentInfo deployment;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final uptime = deployment.uptime;

    return FactList(
      facts: <Fact>[
        Fact(
          label: 'deployment.application'.tr(),
          // Names, versions and SHAs are identifiers: shown as the server
          // spelled them, never translated.
          child: SelectableText(
            deployment.applicationName,
            style: theme.textTheme.bodyMedium,
          ),
        ),
        Fact(
          label: 'deployment.environment'.tr(),
          child: Text(
            deployment.environment,
            style: theme.textTheme.bodyMedium,
          ),
        ),
        Fact(
          label: 'deployment.version'.tr(),
          child: SelectableText(
            deployment.version,
            style: theme.textTheme.bodyMedium,
          ),
        ),
        Fact(
          label: 'deployment.informationalVersion'.tr(),
          child: SelectableText(
            deployment.informationalVersion,
            style: theme.textTheme.bodySmall,
          ),
        ),
        Fact(
          label: 'deployment.framework'.tr(),
          child: Text(
            deployment.frameworkDescription,
            style: theme.textTheme.bodySmall,
          ),
        ),
        Fact(
          label: 'deployment.os'.tr(),
          child: Text(
            deployment.osDescription,
            style: theme.textTheme.bodySmall,
          ),
        ),
        Fact(
          label: 'deployment.architecture'.tr(),
          child: Text(
            deployment.processArchitecture,
            style: theme.textTheme.bodySmall,
          ),
        ),
        Fact(
          label: 'deployment.containerized'.tr(),
          child: Text(
            deployment.isContainerized ? 'common.yes'.tr() : 'common.no'.tr(),
            style: theme.textTheme.bodyMedium,
          ),
        ),
        Fact(
          label: 'deployment.databaseProvider'.tr(),
          child: Text(
            deployment.databaseProvider,
            style: theme.textTheme.bodyMedium,
          ),
        ),
        Fact(
          label: 'deployment.startedAt'.tr(),
          child: TimestampText(timestamp: deployment.startedAtUtc),
        ),
        if (uptime != null)
          Fact(
            label: 'deployment.uptime'.tr(),
            child: Text(
              'common.durationHms'.tr(
                args: [
                  '${uptime.inHours}',
                  '${uptime.inMinutes.remainder(60)}',
                  '${uptime.inSeconds.remainder(60)}',
                ],
              ),
              style: theme.textTheme.bodyMedium,
            ),
          ),
        if (deployment.gitCommit != null)
          Fact(
            label: 'deployment.gitCommit'.tr(),
            child: SelectableText(
              deployment.gitCommit!,
              style: theme.textTheme.bodySmall,
            ),
          ),
        if (deployment.gitBranch != null)
          Fact(
            label: 'deployment.gitBranch'.tr(),
            child: SelectableText(
              deployment.gitBranch!,
              style: theme.textTheme.bodySmall,
            ),
          ),
        if (deployment.buildNumber != null)
          Fact(
            label: 'deployment.buildNumber'.tr(),
            child: SelectableText(
              deployment.buildNumber!,
              style: theme.textTheme.bodySmall,
            ),
          ),
        if (deployment.buildTimestamp != null)
          Fact(
            label: 'deployment.buildTimestamp'.tr(),
            child: SelectableText(
              deployment.buildTimestamp!,
              style: theme.textTheme.bodySmall,
            ),
          ),
      ],
    );
  }
}

/// Database provider, readiness and resilience counters.
///
/// The endpoint returns no connection string and no file path, so there is
/// nothing to redact here — and nothing is invented to fill the gap.
class DatabaseInfoPanel extends StatelessWidget {
  const DatabaseInfoPanel({required this.database, super.key});

  final DatabaseInfo database;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FactList(
          facts: <Fact>[
            Fact(
              label: 'database.provider'.tr(),
              child: Text(
                database.providerName,
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'database.ready'.tr(),
              child: HealthIndicator(
                label: 'database.ready'.tr(),
                healthy: database.isReady,
                degraded: !database.isReady,
              ),
            ),
            Fact(
              label: 'database.commandTimeout'.tr(),
              child: Text(
                'common.seconds'.tr(
                  args: ['${database.commandTimeoutSeconds}'],
                ),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'database.retries'.tr(),
              child: Text(
                database.retriesEnabled
                    ? 'database.retriesEnabled'.plural(
                        database.maxRetryAttempts,
                      )
                    : 'common.disabled'.tr(),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'database.optimisticConcurrency'.tr(),
              child: Text(
                database.optimisticConcurrencyEnabled
                    ? 'common.enabled'.tr()
                    : 'common.disabled'.tr(),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'database.operations'.tr(),
              child: Text(
                '${database.operationsCount}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'database.failures'.tr(),
              child: Text(
                '${database.failuresCount}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: database.hasFailures ? theme.colorScheme.error : null,
                ),
              ),
            ),
            Fact(
              label: 'database.averageDuration'.tr(),
              child: Text(
                'common.milliseconds'.tr(
                  args: [database.averageDurationMs.toStringAsFixed(1)],
                ),
                style: theme.textTheme.bodyMedium,
              ),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.md),
        Text(
          'database.counters'.tr(),
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        Text(
          'database.countersDetail'.tr(
            args: [
              '${database.retriesCount}',
              '${database.transientFailuresCount}',
              '${database.concurrencyConflictsCount}',
              '${database.busyFailuresCount}',
              '${database.lockedFailuresCount}',
              '${database.transactionFailuresCount}',
            ],
          ),
          style: theme.textTheme.bodySmall,
        ),
        const SizedBox(height: AppSpacing.md),
        UnavailableNote(
          title: 'database.migrations'.tr(),
          reason: 'database.migrationsUnavailable'.tr(),
        ),
      ],
    );
  }
}

/// Scheduler **activity**, never worker health.
///
/// The backend exposes what the dispatch loop did; it exposes nothing about
/// whether a Worker process is alive (`G-WORKER-HEALTH`). A cycle count of
/// zero is therefore not "unhealthy" and a non-zero one is not "healthy" —
/// the panel reports the counters and says exactly that.
class SchedulerActivityPanel extends StatelessWidget {
  const SchedulerActivityPanel({required this.scheduler, super.key});

  final SchedulerStatus scheduler;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final lastCycle = scheduler.lastCycleAtUtc;
    final enabledLabel = scheduler.enabled
        ? 'common.enabled'.tr()
        : 'common.disabled'.tr();

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FactList(
          facts: <Fact>[
            Fact(
              label: 'scheduler.enabled'.tr(),
              child: Semantics(
                label: '${'scheduler.enabled'.tr()}: $enabledLabel',
                excludeSemantics: true,
                child: Text(enabledLabel, style: theme.textTheme.bodyMedium),
              ),
            ),
            Fact(
              label: 'scheduler.maxConcurrent'.tr(),
              child: Text(
                '${scheduler.maxConcurrentTasks}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'scheduler.lastCycle'.tr(),
              child: lastCycle == null
                  ? Text(
                      'scheduler.noCycleYet'.tr(),
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: theme.colorScheme.onSurfaceVariant,
                      ),
                    )
                  : TimestampText(timestamp: lastCycle),
            ),
            Fact(
              label: 'scheduler.lastCycleCounts'.tr(),
              child: Text(
                'scheduler.lastCycleDetail'.tr(
                  args: [
                    '${scheduler.lastCycleDispatchedCount}',
                    '${scheduler.lastCycleFailureCount}',
                    '${scheduler.lastCycleSkippedDueToConcurrency}',
                  ],
                ),
                style: theme.textTheme.bodySmall,
              ),
            ),
            Fact(
              label: 'scheduler.totals'.tr(),
              child: Text(
                'scheduler.totalsDetail'.tr(
                  args: [
                    '${scheduler.totalDispatched}',
                    '${scheduler.totalFailures}',
                  ],
                ),
                style: theme.textTheme.bodySmall,
              ),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.md),
        UnavailableNote(
          title: 'scheduler.notWorkerHealth'.tr(),
          reason: 'scheduler.notWorkerHealthReason'.tr(),
        ),
      ],
    );
  }
}

/// Safe configuration metadata: flags, names and counts.
///
/// The endpoint is already sanitized server-side — it returns provider
/// *names* and whether a secret reference exists, never a key, a token or a
/// connection string — and this panel adds no field of its own. The secret
/// reference name is shown because it is a lookup name (`OpenRouter:ApiKey`),
/// not a secret value.
///
/// Model routing is not exposed by any endpoint (`G-ROUTING`), so the panel
/// says so rather than reconstructing routes from provider names.
class ConfigurationInfoPanel extends StatelessWidget {
  const ConfigurationInfoPanel({required this.configuration, super.key});

  final ConfigurationInfo configuration;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FactList(
          facts: <Fact>[
            Fact(
              label: 'configuration.environment'.tr(),
              child: Text(
                configuration.environmentName,
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'configuration.valid'.tr(),
              child: HealthIndicator(
                label: 'configuration.valid'.tr(),
                healthy: configuration.isValid,
                degraded: !configuration.isValid,
              ),
            ),
            Fact(
              label: 'configuration.database'.tr(),
              child: Text(
                configuration.databaseConfigured
                    ? 'configuration.databaseConfigured'.tr(
                        args: [configuration.databaseProvider],
                      )
                    : 'configuration.databaseNotConfigured'.tr(),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'configuration.secretManagement'.tr(),
              child: Text(
                configuration.secretManagementEnabled
                    ? configuration.secretProviderName
                    : 'common.disabled'.tr(),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'configuration.operationalEndpoints'.tr(),
              child: Text(
                configuration.operationalEndpointsSecured
                    ? 'configuration.operationalSecured'.tr(
                        args: [configuration.operationalKeyHeader],
                      )
                    : 'configuration.operationalOpen'.tr(),
                style: theme.textTheme.bodySmall,
              ),
            ),
            Fact(
              label: 'configuration.rateLimiting'.tr(),
              child: Text(
                configuration.rateLimitingEnabled
                    ? 'common.enabled'.tr()
                    : 'common.disabled'.tr(),
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'configuration.transport'.tr(),
              child: Text(
                'configuration.transportDetail'.tr(
                  args: [
                    configuration.httpsRedirectEnabled
                        ? 'common.enabled'.tr()
                        : 'common.disabled'.tr(),
                    configuration.hstsEnabled
                        ? 'common.enabled'.tr()
                        : 'common.disabled'.tr(),
                    configuration.corsEnabled
                        ? 'common.enabled'.tr()
                        : 'common.disabled'.tr(),
                  ],
                ),
                style: theme.textTheme.bodySmall,
              ),
            ),
            Fact(
              label: 'configuration.maxBodySize'.tr(),
              child: Text(
                '${configuration.maxRequestBodySizeBytes}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
          ],
        ),
        const SizedBox(height: AppSpacing.md),
        Text(
          'configuration.providers'.tr(),
          style: theme.textTheme.labelMedium?.copyWith(
            color: theme.colorScheme.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: AppSpacing.xs),
        if (configuration.providers.isEmpty)
          Text(
            'configuration.noProviders'.tr(),
            style: theme.textTheme.bodySmall?.copyWith(
              color: theme.colorScheme.onSurfaceVariant,
            ),
          )
        else
          for (final provider in configuration.providers)
            Padding(
              padding: const EdgeInsets.only(bottom: AppSpacing.xs),
              child: Text(
                'configuration.providerDetail'.tr(
                  args: [
                    provider.name,
                    provider.enabled
                        ? 'common.enabled'.tr()
                        : 'common.disabled'.tr(),
                    provider.hasSecretReference
                        ? (provider.secretReferenceName ??
                              'configuration.secretReferencePresent'.tr())
                        : 'configuration.noSecretReference'.tr(),
                  ],
                ),
                style: theme.textTheme.bodySmall,
              ),
            ),
        if (configuration.resilienceProfileNames.isNotEmpty) ...[
          const SizedBox(height: AppSpacing.sm),
          Text(
            'configuration.resilienceProfiles'.tr(
              args: [configuration.resilienceProfileNames.join(', ')],
            ),
            style: theme.textTheme.bodySmall,
          ),
        ],
        const SizedBox(height: AppSpacing.md),
        UnavailableNote(
          title: 'configuration.routing'.tr(),
          reason: 'configuration.routingUnavailable'.tr(),
        ),
      ],
    );
  }
}

/// Agent run counters, exactly the fields `/api/agents/metrics` returns.
///
/// No token usage, no model cost, no provider latency: the endpoint returns
/// none of them, and a zero would read as a measurement rather than as an
/// absence (`G-RUN-TELEMETRY`).
class AgentMetricsPanel extends StatelessWidget {
  const AgentMetricsPanel({required this.metrics, super.key});

  final AgentMetrics metrics;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final average = metrics.averageDuration;
    final inFlight = 'metrics.inFlightDetail'.tr(
      args: ['${metrics.queued}', '${metrics.running}'],
    );

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        FactList(
          facts: <Fact>[
            Fact(
              label: 'metrics.inFlight'.tr(),
              child: Semantics(
                label: '${'metrics.inFlight'.tr()}: $inFlight',
                excludeSemantics: true,
                child: Text(inFlight, style: theme.textTheme.bodyMedium),
              ),
            ),
            Fact(
              label: 'metrics.created'.tr(),
              child: Text(
                '${metrics.created}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'metrics.started'.tr(),
              child: Text(
                '${metrics.started}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'metrics.succeeded'.tr(),
              child: Text(
                '${metrics.succeeded}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'metrics.failed'.tr(),
              child: Text(
                '${metrics.failed}',
                style: theme.textTheme.bodyMedium?.copyWith(
                  color: metrics.failed > 0 ? theme.colorScheme.error : null,
                ),
              ),
            ),
            Fact(
              label: 'metrics.timedOut'.tr(),
              child: Text(
                '${metrics.timedOut}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'metrics.cancelled'.tr(),
              child: Text(
                '${metrics.cancelled}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            Fact(
              label: 'metrics.workerClaimSuccess'.tr(),
              child: Text(
                '${metrics.workerClaimSuccess}',
                style: theme.textTheme.bodyMedium,
              ),
            ),
            if (average != null)
              Fact(
                label: 'metrics.averageDuration'.tr(),
                child: Text(
                  'common.milliseconds'.tr(args: ['${average.inMilliseconds}']),
                  style: theme.textTheme.bodyMedium,
                ),
              ),
          ],
        ),
        const SizedBox(height: AppSpacing.md),
        UnavailableNote(
          title: 'metrics.telemetry'.tr(),
          reason: 'metrics.telemetryUnavailable'.tr(),
        ),
      ],
    );
  }
}
