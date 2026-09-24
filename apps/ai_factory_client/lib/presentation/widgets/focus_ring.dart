import 'package:flutter/material.dart';

/// A visible 2 dp outline around a custom tappable surface while it holds
/// keyboard focus.
///
/// Material buttons get their ring from the theme (`AppTheme`); an `InkWell`
/// tile only tints its background on focus, which is too faint to find on a
/// busy screen. The ring follows the keyboard only — a mouse click does not
/// draw it — and it is drawn in the foreground, so it never shifts layout.
class FocusRing extends StatefulWidget {
  const FocusRing({required this.child, this.borderRadius, super.key});

  final Widget child;
  final BorderRadius? borderRadius;

  @override
  State<FocusRing> createState() => _FocusRingState();
}

class _FocusRingState extends State<FocusRing> {
  bool _visible = false;

  void _onFocusChange(bool hasFocus) {
    final keyboard =
        FocusManager.instance.highlightMode == FocusHighlightMode.traditional;
    final visible = hasFocus && keyboard;
    if (visible != _visible) setState(() => _visible = visible);
  }

  @override
  Widget build(BuildContext context) {
    return Focus(
      // Observes the tile's own focus node; takes no focus itself.
      canRequestFocus: false,
      skipTraversal: true,
      onFocusChange: _onFocusChange,
      child: DecoratedBox(
        position: DecorationPosition.foreground,
        decoration: _visible
            ? BoxDecoration(
                border: Border.all(
                  color: Theme.of(context).colorScheme.primary,
                  width: 2,
                ),
                borderRadius: widget.borderRadius,
              )
            : const BoxDecoration(),
        child: widget.child,
      ),
    );
  }
}
