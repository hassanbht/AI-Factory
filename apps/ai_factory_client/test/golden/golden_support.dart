import 'dart:io';

import 'package:flutter/services.dart';

/// Loads Roboto and the Material icon font into the test engine.
///
/// A widget test otherwise renders every glyph with the "FlutterTest" box
/// font: layout is still checked, but a German label and an English one look
/// alike and an icon is a square. Goldens are reviewed by people, so they
/// render the real faces. Both ship inside the Flutter SDK cache
/// (`bin/cache/artifacts/material_fonts`) — no font is vendored into this
/// repository and nothing is downloaded.
Future<void> loadGoldenFonts() async {
  final directory = _materialFontsDirectory();

  final roboto = FontLoader('Roboto');
  for (final face in const [
    'roboto-light.ttf',
    'roboto-regular.ttf',
    'roboto-medium.ttf',
    'roboto-bold.ttf',
    'roboto-italic.ttf',
  ]) {
    roboto.addFont(_read('${directory.path}/$face'));
  }
  await roboto.load();

  final icons = FontLoader('MaterialIcons')
    ..addFont(_read('${directory.path}/materialicons-regular.otf'));
  await icons.load();
}

Future<ByteData> _read(String path) async =>
    ByteData.sublistView(Uint8List.fromList(File(path).readAsBytesSync()));

/// `FLUTTER_ROOT` when the tool exports it; otherwise the SDK is found from
/// the test runner itself, which lives under `<sdk>/bin/cache/artifacts`.
Directory _materialFontsDirectory() {
  final root = Platform.environment['FLUTTER_ROOT'];
  if (root != null && root.isNotEmpty) {
    final candidate = Directory('$root/bin/cache/artifacts/material_fonts');
    if (candidate.existsSync()) return candidate;
  }

  var current = File(Platform.resolvedExecutable).parent;
  while (current.parent.path != current.path) {
    final candidate = Directory('${current.path}/artifacts/material_fonts');
    if (candidate.existsSync()) return candidate;
    current = current.parent;
  }
  throw StateError(
    'Material fonts not found in the Flutter SDK cache. '
    'Run `flutter precache` and retry.',
  );
}
