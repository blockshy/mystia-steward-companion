import '../app/workbench_model.dart';
import '../ui/page_activity.dart';

/// 将经营方案的只读展示状态投影到页头，不参与执行许可或业务当前性判断。
///
/// isCalculating 只说明服务端求解函数此刻正在运行，不保证正在计算本行，也不证明
/// 旧方案已覆盖最新库存或配置。因此只在当前视图确有订单时展示计算进度；计算停止
/// 后仍按可见订单在页头汇总“待更新/参考”，不往各卡片插入临时文字行；绝不能用它
/// 替代完整 current、租约或主设备门禁。
/// 调用方须传入筛选后、所选客人类型内的现场订单；隐藏页和诊断任务不冒充现场订单。
PageActivity? servicePlanActivity({
  required bool connected,
  required DataMap business,
  required Iterable<DataMap> visibleOrders,
}) {
  if (!connected) return null;
  final error = business['error'];
  if (error is String && error.trim().isNotEmpty) {
    return PageActivity(message: '方案计算失败：${error.trim()}', error: true);
  }
  final orders = visibleOrders
      .where((row) => object(row['order']).isNotEmpty)
      .toList(growable: false);
  if (orders.isEmpty) return null;
  final waiting = orders.any((row) => row['presentationPending'] == true);
  final reference = orders.any(
    (row) =>
        row['presentationPending'] != true &&
        (row['presentationStale'] == true || business['isCurrent'] != true),
  );
  final explanations = <String>[
    if (waiting) '方案待更新',
    if (reference) '方案仅供参考，执行状态待同步',
  ];
  if (business['isCalculating'] == true) {
    return PageActivity(
      message: '正在计算方案',
      details: ['正在计算方案', ...explanations].join('\n'),
      busy: true,
    );
  }
  if (explanations.isEmpty) return null;
  return PageActivity(
    message: explanations.join(' · '),
    details: explanations.join('\n'),
  );
}
