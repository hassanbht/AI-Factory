import 'package:equatable/equatable.dart';

import '../errors/app_error.dart';

/// The single view state used by every screen (`docs/flutter-architecture.md`
/// §7). A screen must handle all seven cases — a blank screen is a defect.
///
/// [Partial] exists because aggregate screens read several independent
/// endpoints, and one failing diagnostics call must never blank out the data
/// that did load.
///
/// Value equality matters: a poll that brings back exactly what is on screen
/// produces an equal state, and GetX does not notify for an equal value — so
/// an unchanged cycle rebuilds nothing, however long the list.
sealed class ViewState<T> extends Equatable {
  const ViewState();

  @override
  List<Object?> get props => const [];

  /// Data already available, whatever the current phase is.
  T? get dataOrNull => switch (this) {
    Success<T>(:final data) => data,
    Refreshing<T>(:final data) => data,
    Partial<T>(:final data) => data,
    Failure<T>(:final stale) => stale,
    _ => null,
  };

  bool get isBusy => this is Loading<T> || this is Refreshing<T>;

  R map<R>({
    required R Function() idle,
    required R Function() loading,
    required R Function(T data) refreshing,
    required R Function(T data) success,
    required R Function(T data, List<AppError> failures) partial,
    required R Function() empty,
    required R Function(AppError error, T? stale) failure,
  }) {
    return switch (this) {
      Idle<T>() => idle(),
      Loading<T>() => loading(),
      Refreshing<T>(:final data) => refreshing(data),
      Success<T>(:final data) => success(data),
      Partial<T>(:final data, :final failures) => partial(data, failures),
      Empty<T>() => empty(),
      Failure<T>(:final error, :final stale) => failure(error, stale),
    };
  }
}

/// Nothing requested yet.
class Idle<T> extends ViewState<T> {
  const Idle();
}

/// First load — nothing to show yet, so a skeleton is rendered.
class Loading<T> extends ViewState<T> {
  const Loading();
}

/// A poll or manual refresh is in flight while data stays on screen.
class Refreshing<T> extends ViewState<T> {
  const Refreshing(this.data);

  final T data;

  @override
  List<Object?> get props => [data];
}

class Success<T> extends ViewState<T> {
  const Success(this.data);

  final T data;

  @override
  List<Object?> get props => [data];
}

/// Some sources loaded, others failed.
class Partial<T> extends ViewState<T> {
  const Partial(this.data, this.failures);

  final T data;
  final List<AppError> failures;

  @override
  List<Object?> get props => [data, failures];
}

/// The request succeeded and the result is legitimately empty.
class Empty<T> extends ViewState<T> {
  const Empty();
}

class Failure<T> extends ViewState<T> {
  const Failure(this.error, {this.stale});

  final AppError error;

  /// Last known data, shown as stale rather than discarded.
  final T? stale;

  @override
  List<Object?> get props => [error, stale];
}
