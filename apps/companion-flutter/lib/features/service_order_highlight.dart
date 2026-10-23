import 'package:flutter/widgets.dart';

import '../app/workbench_model.dart';

/// 与 C# RuntimeTargetHighlightColor 保持同一默认调色板，配置入口和订单展示
/// 共用此处定义，避免重置颜色后两处出现差异。颜色只表示订单通道，不表示执行许可。
const normalOrderHighlightHex = '#5FACD3';
const rareOrderHighlightHex = '#FFDB2E';

/// 普/稀客订单按业务通道取色，不能依据头像类型分组：复制稀客仍可能提交普通订单。
/// 非法/尚未加载的配置仅使用对应默认色，不把缺值当黑色；调用方负责背景透明度。
Color serviceOrderHighlight(WorkbenchModel model, {required bool rare}) {
  final value =
      model.profile[rare
          ? 'rareTargetHighlightColor'
          : 'normalTargetHighlightColor'];
  final hex = value is String && RegExp(r'^#[0-9a-fA-F]{6}$').hasMatch(value)
      ? value
      : rare
      ? rareOrderHighlightHex
      : normalOrderHighlightHex;
  return Color(0xff000000 | int.parse(hex.substring(1), radix: 16));
}
