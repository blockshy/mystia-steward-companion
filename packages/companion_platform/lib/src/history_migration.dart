import 'dart:convert';

/// 用户显式提供的历史偏好文件。只解析白名单，不读取浏览器数据库，不导入身份或运行许可。
class HistoryMigration {
  static const _prefix = 'mystia-steward-companion-';
  static const _keys = <String>{
    'theme-mode',
    'font-scale-percent',
    'background-opacity',
    'content-opacity',
    'mod-tab',
    'service-focus-compact',
    'service-focus-recipe-limit',
    'service-focus-beverage-limit',
    'custom-recipe-group-mode',
    'mission-list-module-enabled',
    'rare-guest-invitation-module-enabled',
    'rare-guest-invitation-scope',
    'rare-guest-invitation-levels',
    'focus-switch-behavior',
    'focus-switch-cooldown-ms',
    'always-on-top',
    'gamepad-navigation',
    'show-debug-details',
  };

  /// 输入格式为 {schemaVersion:1, preferences:{旧键名:字符串}}。
  /// 拒绝密钥字段、未知字段和超大输入，失败不返回部分可应用结果。
  static Map<String, String> parse(String source) {
    if (utf8.encode(source).length > 65536)
      throw const FormatException('历史配置文件过大。');
    final value = jsonDecode(source);
    if (value is! Map<String, dynamic> ||
        value['schemaVersion'] != 1 ||
        value.keys.any(
          (key) => key != 'schemaVersion' && key != 'preferences',
        ) ||
        value['preferences'] is! Map<String, dynamic>) {
      throw const FormatException('历史偏好格式无效。');
    }
    final result = <String, String>{};
    for (final entry
        in (value['preferences'] as Map<String, dynamic>).entries) {
      final key = entry.key.startsWith(_prefix)
          ? entry.key.substring(_prefix.length)
          : entry.key;
      if (!_keys.contains(key) ||
          entry.value is! String ||
          (entry.value as String).length > 2048) {
        throw const FormatException('历史偏好包含未允许或格式错误的字段。');
      }
      if (result.containsKey(key)) throw const FormatException('历史偏好存在重复键。');
      result[key] = _normalize(key, entry.value as String);
    }
    return Map.unmodifiable(result);
  }

  /// 旧客户端既存 1/0 也接受 true/false。统一为 Dart 字符串布尔值，拒绝越界值而不猜测。
  static String _normalize(String key, String value) {
    const booleans = {
      'service-focus-compact',
      'mission-list-module-enabled',
      'rare-guest-invitation-module-enabled',
      'always-on-top',
      'gamepad-navigation',
      'show-debug-details',
    };
    if (booleans.contains(key)) {
      if (value == '1' || value == 'true') return 'true';
      if (value == '0' || value == 'false') return 'false';
      throw const FormatException('历史偏好布尔值无效。');
    }
    const options = {
      'theme-mode': {'light', 'dark', 'system'},
      'mod-tab': {
        'overview',
        'recommendations',
        'service',
        'extensions',
        'logs',
        'settings',
      },
      'custom-recipe-group-mode': {'recipe', 'customer'},
      'rare-guest-invitation-scope': {'current', 'all'},
      'focus-switch-behavior': {'hide', 'keep-visible'},
    };
    if (options.containsKey(key)) {
      if (!options[key]!.contains(value))
        throw const FormatException('历史偏好枚举值无效。');
      return value;
    }
    if (key == 'rare-guest-invitation-levels') {
      if (value.isEmpty) return '';
      final values = value.split(',').map(int.tryParse).toList();
      if (values.any((item) => item == null || item < 0 || item > 2147483647)) {
        throw const FormatException('历史邀请等级无效。');
      }
      final sorted = values.cast<int>().toSet().toList()..sort();
      return sorted.join(',');
    }
    final number = double.tryParse(value);
    final (minimum, maximum, integral) = switch (key) {
      'font-scale-percent' => (90.0, 130.0, true),
      'background-opacity' => (0.2, 1.0, false),
      'content-opacity' => (0.35, 1.0, false),
      'focus-switch-cooldown-ms' => (250.0, 2000.0, true),
      _ => (1.0, 20.0, true),
    };
    if (number == null ||
        !number.isFinite ||
        number < minimum ||
        number > maximum ||
        (integral && number != number.truncateToDouble()) ||
        (key == 'font-scale-percent' && number % 5 != 0)) {
      throw const FormatException('历史偏好数值超出允许范围。');
    }
    return integral ? number.toInt().toString() : number.toString();
  }
}
