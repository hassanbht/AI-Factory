import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';
import 'package:get/get.dart' hide Trans;

import '../../core/polling/polling_coordinator.dart';
import '../../core/theme/app_motion.dart';
import '../../core/theme/app_spacing.dart';
import '../../core/utils/formatters.dart';

/// Tells [LiveUpdatesScope] when its page is covered by another page.
///
/// Registered on the application navigator. Typed to [PageRoute] on purpose:
/// a confirmation dialog is a route too, and opening one must not pause the
/// screen it is confirming something on.
final RouteObserver<PageRoute<dynamic>> liveRouteObserver =
    RouteObserver<PageRoute<dynamic>>();

/// Runs [poller] for exactly as long as this page is on screen.
///
/// Mounting starts polling, disposal stops it, and a page pushed on top
/// pauses it until that page is popped again — which refreshes immediately.
/// This widget only reports visibility; it never creates a timer.
class LiveUpdatesScope extends StatefulWidget {
  const LiveUpdatesScope({
    required this.poller,
    required this.child,
    super.key,
  });

  final PollingCoordinator poller;
  final Widget child;

  @override
  State<LiveUpdatesScope> createState() => _LiveUpdatesScopeState();
}

class _LiveUpdatesScopeState extends State<LiveUpdatesScope> with RouteAware {
  PageRoute<dynamic>? _route;

  @override
  void initState() {
    super.initState();
    widget.poller.start();
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    final route = ModalRoute.of(context);
    if (route == _route) return;
    liveRouteObserver.unsubscribe(this);
    _route = route is PageRoute<dynamic> ? route : null;
    if (_route != null) liveRouteObserver.subscribe(this, _route!);
  }

  @override
  void didUpdateWidget(LiveUpdatesScope oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.poller == widget.poller) return;
    oldWidget.poller.stop();
    widget.poller.start();
  }

  @override
  void didPushNext() => widget.poller.pause(PollPause.hidden);

  @override
  void didPopNext() => widget.poller.resume(PollPause.hidden);

  @override
  void dispose() {
    liveRouteObserver.unsubscribe(this);
    widget.poller.stop();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}

/// A compact line stating how fresh the screen's data is.
///
/// Wording is deliberately modest: polling is not a connection and delivers
/// no events, so the indicator says "live updates" and "last updated", never
/// "connected".
///
/// Accessibility: the status is a live region, but it carries the *settled*
/// status — the one that ignores a request in flight — so a routine poll is
/// never announced. Only real transitions are: going offline, a failure,
/// access required, and recovery.
class LiveStatusIndicator extends StatelessWidget {
  const LiveStatusIndicator({
    required this.poller,
    this.onRetry,
    this.onEnterKey,
    super.key,
  });

  final PollingCoordinator poller;

  /// Defaults to an immediate refresh through [poller].
  final VoidCallback? onRetry;

  /// Offered while the operational key is missing or rejected.
  final VoidCallback? onEnterKey;

  static String labelKey(LiveStatus status) => switch (status) {
    LiveStatus.idle => 'live.status.idle',
    LiveStatus.live => 'live.status.live',
    LiveStatus.refreshing => 'live.status.refreshing',
    LiveStatus.offline => 'live.status.offline',
    LiveStatus.retrying => 'live.status.retrying',
    LiveStatus.paused => 'live.status.paused',
    LiveStatus.accessRequired => 'live.status.accessRequired',
    LiveStatus.halted => 'live.status.halted',
    LiveStatus.stopped => 'live.status.stopped',
  };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final locale = context.locale.toString();

    return Obx(() {
      final status = poller.status.value;
      final updated = poller.lastSuccessfulUpdate.value;
      final settled = poller.settledStatus;

      // A poll in flight changes the icon, not the words: the line would
      // otherwise flicker every few seconds. Only the very first load, with
      // nothing settled yet, says "refreshing".
      final shown =
          status == LiveStatus.refreshing && settled == LiveStatus.idle
          ? LiveStatus.refreshing
          : settled;

      final color = switch (shown) {
        LiveStatus.offline ||
        LiveStatus.retrying ||
        LiveStatus.halted ||
        LiveStatus.accessRequired => scheme.error,
        LiveStatus.live => scheme.primary,
        _ => scheme.onSurfaceVariant,
      };
      final labelStyle = theme.textTheme.labelMedium?.copyWith(color: color);
      final metaStyle = theme.textTheme.labelSmall?.copyWith(
        color: scheme.onSurfaceVariant,
      );

      final canRetry =
          shown == LiveStatus.offline ||
          shown == LiveStatus.retrying ||
          shown == LiveStatus.halted;

      return Wrap(
        spacing: AppSpacing.sm,
        runSpacing: AppSpacing.xs,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Semantics(
            container: true,
            liveRegion: true,
            label: labelKey(settled).tr(),
            excludeSemantics: true,
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                // Reduced motion: the in-flight spinner is decoration (the
                // cycle is also exposed below), so a static icon stands in.
                if (status == LiveStatus.refreshing &&
                    !AppMotion.reduced(context))
                  const SizedBox(
                    width: 12,
                    height: 12,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                else
                  Icon(
                    status == LiveStatus.refreshing ? Icons.sync : _icon(shown),
                    size: 14,
                    color: color,
                  ),
                const SizedBox(width: AppSpacing.xs),
                Flexible(child: Text(labelKey(shown).tr(), style: labelStyle)),
              ],
            ),
          ),
          if (status == LiveStatus.refreshing)
            // Not a live region: reachable when the operator looks for it,
            // never read out on its own.
            Semantics(
              label: 'live.status.refreshing'.tr(),
              child: const SizedBox.shrink(),
            ),
          Text(
            updated == null
                ? 'live.neverUpdated'.tr()
                : 'live.lastUpdated'.tr(
                    args: [Formatters.time(updated, locale)],
                  ),
            style: metaStyle,
          ),
          if (canRetry)
            TextButton(
              onPressed: onRetry ?? poller.refreshNow,
              child: Text('live.retryNow'.tr()),
            ),
          if (shown == LiveStatus.accessRequired && onEnterKey != null)
            TextButton(
              onPressed: onEnterKey,
              child: Text('live.enterKey'.tr()),
            ),
        ],
      );
    });
  }

  static IconData _icon(LiveStatus status) => switch (status) {
    LiveStatus.idle => Icons.hourglass_empty,
    LiveStatus.live => Icons.sync,
    LiveStatus.refreshing => Icons.sync,
    LiveStatus.offline => Icons.cloud_off_outlined,
    LiveStatus.retrying => Icons.sync_problem,
    LiveStatus.paused => Icons.pause_circle_outline,
    LiveStatus.accessRequired => Icons.lock_outline,
    LiveStatus.halted => Icons.error_outline,
    LiveStatus.stopped => Icons.sync_disabled,
  };
}
