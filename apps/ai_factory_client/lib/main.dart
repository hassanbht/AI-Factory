import 'package:easy_localization/easy_localization.dart';
import 'package:flutter/material.dart';

import 'app/app.dart';
import 'app/app_bindings.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Translation assets and the persisted locale must be available before the
  // first frame, otherwise the shell would flash an untranslated state.
  await EasyLocalization.ensureInitialized();
  await AppBindings.initPreRunApp();

  runApp(const AiFactoryAppRoot());
}
