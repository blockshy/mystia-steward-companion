import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service_plan_activity.dart';

void main() {
  const matched = <String, Object?>{
    'order': {'traceId': 'R-1'},
    'presentationPending': false,
    'presentationStale': false,
  };
  const waiting = <String, Object?>{
    'order': {'traceId': 'R-2'},
    'presentationPending': true,
  };
  final stale = <String, Object?>{...matched, 'presentationStale': true};

  test('完整帧pending不等于实际计算，已匹配旧方案保持静态参考说明', () {
    final status = servicePlanActivity(
      connected: true,
      business: {'pending': true, 'isCurrent': false},
      visibleOrders: [matched],
    );
    expect(status?.busy, isFalse);
    expect(status?.error, isFalse);
    expect(status?.message, '方案仅供参考，执行状态待同步');
  });

  test('实际计算有可见订单才转圈，并保留旧结果参考状态到详情', () {
    final status = servicePlanActivity(
      connected: true,
      business: {'isCalculating': true, 'isCurrent': false},
      visibleOrders: [waiting, stale],
    );
    expect(status?.busy, isTrue);
    expect(status?.message, '正在计算方案');
    expect(status?.details, contains('方案待更新'));
    expect(status?.details, contains('方案仅供参考，执行状态待同步'));
  });

  test('空/隐藏订单和诊断任务不驱动计算动画', () {
    for (final rows in <List<DataMap>>[
      <DataMap>[],
      [
        {'title': '历史自动化任务', 'orderKey': '旧订单'},
      ],
    ]) {
      expect(
        servicePlanActivity(
          connected: true,
          business: {'isCalculating': true, 'pending': true},
          visibleOrders: rows,
        ),
        isNull,
      );
    }
  });

  test('新订单尚无方案时静态待更新，不凭pending字段制造计算活动', () {
    final status = servicePlanActivity(
      connected: true,
      business: {'pending': true, 'isCurrent': false},
      visibleOrders: [waiting],
    );
    expect(status?.message, '方案待更新');
    expect(status?.busy, isFalse);
  });

  test('混合新单与旧参考行分别说明，最新current也不能抹掉行自身stale', () {
    final status = servicePlanActivity(
      connected: true,
      business: {'isCurrent': true},
      visibleOrders: [waiting, stale],
    );
    expect(status?.message, contains('方案待更新'));
    expect(status?.message, contains('方案仅供参考，执行状态待同步'));
    expect(status?.busy, isFalse);
  });

  test('已完成的阻断结果不是永久计算，已知当前结果不新增提示', () {
    expect(
      servicePlanActivity(
        connected: true,
        business: {'isCurrent': true},
        visibleOrders: [
          {...matched, 'presentationIssue': '没有满足点单的料理'},
        ],
      ),
      isNull,
    );
  });

  test('明确计算错误优先于busy，空列表仍保留错误，不冒称正在计算', () {
    for (final rows in <List<DataMap>>[
      <DataMap>[],
      [stale],
    ]) {
      final status = servicePlanActivity(
        connected: true,
        business: {'isCalculating': true, 'error': '合成失败'},
        visibleOrders: rows,
      );
      expect(status?.message, '方案计算失败：合成失败');
      expect(status?.busy, isFalse);
      expect(status?.error, isTrue);
    }
  });

  test('断线不把最后一次计算或失败带回当前页头', () {
    expect(
      servicePlanActivity(
        connected: false,
        business: {'isCalculating': true, 'error': '旧错误'},
        visibleOrders: [stale],
      ),
      isNull,
    );
  });
}
