import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'app/live_workbench_model.dart';
import 'app/steward_app.dart';

/// 原生 runner 在进入 Dart 前完成单例与控制通道初始化；UI 立即启动并展示初始化错误。
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final model = LiveWorkbenchModel();
  runApp(
    ProviderScope(
      overrides: [workbenchProvider.overrideWithValue(model)],
      child: const StewardApp(),
    ),
  );
  unawaited(model.initialize());
}
