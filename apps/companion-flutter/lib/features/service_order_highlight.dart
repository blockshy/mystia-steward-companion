import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';

/// 颜色由 C# 按经营代次和精确订单身份分配。页面只读取颜色，不按桌号、列表位置
/// 或客人类型自行计算；未取得当前订单映射时使用中性背景，避免借用另一单的颜色。
Color serviceOrderHighlight(
  WorkbenchModel model, {
  required bool rare,
  DataMap order = const {},
}) {
  final generation = model.snapshot['nightBusinessGeneration'];
  final rows = records(object(model.business['gameUiTargets'])['orderColors']);
  final matches = rows
      .where(
        (row) =>
            row['kind'] == (rare ? 'rare' : 'normal') &&
            row['businessGeneration'] == generation &&
            row['traceId'] == order['traceId'] &&
            row['orderLifecycleSequence'] == order['orderLifecycleSequence'] &&
            (rare || row['orderKey'] == order['orderKey']),
      )
      .toList();
  final value = matches.length == 1 ? matches.single['color'] : null;
  if (value is! String || !RegExp(r'^#[0-9A-F]{6}$').hasMatch(value)) {
    return const Color(0xff929AA6);
  }
  return Color(0xff000000 | int.parse(value.substring(1), radix: 16));
}
