import 'dart:io';

import 'package:flutter/services.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 新优先字体的本机测试入口；可变字体本身包含真实 100–900 字重，无需合成粗体。
/// CI 未提供该可选系统字体时由调用方明确跳过真实字形断言，不把 Ahem 当中文证据。
/// 显式环境路径不存在则读取失败，不能将配置错误静默当成正常回退。
String? get preferredStewardTestFontPath {
  final specified = Platform.environment['MYSTIA_UI_NOTO_FONT'];
  if (specified != null) return specified;
  const windowsPath = 'C:/Windows/Fonts/NotoSansSC-VF.ttf';
  return Platform.isWindows && File(windowsPath).existsSync()
      ? windowsPath
      : null;
}

Future<bool> loadPreferredStewardTestFont() async {
  final path = preferredStewardTestFontPath;
  if (path == null) return false;
  final bytes = await File(path).readAsBytes();
  await (FontLoader(
    stewardFontFamily,
  )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
  return true;
}
