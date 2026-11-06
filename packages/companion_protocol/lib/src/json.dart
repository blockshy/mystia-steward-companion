import 'dart:convert';

/// 线路对象只保存 JSON 值。对象进入 DTO 时递归冻结，页面不能修改共享快照。
typedef JsonMap = Map<String, Object?>;

/// 验证对象形状并递归复制、冻结内容，使调用方后续修改原 Map 不会污染 DTO。
/// [field] 仅用于定位格式错误，不应传入凭据或完整响应正文。
JsonMap jsonMap(Object? value, [String field = '响应']) {
  if (value is! Map<String, Object?>) {
    throw FormatException('$field 必须是 JSON 对象。');
  }
  return Map<String, Object?>.unmodifiable(
    value.map((key, item) => MapEntry(key, freezeJson(item))),
  );
}

/// 仅接收 JSON 可表示的标量、字符串键对象及列表；拒绝非有限浮点数和任意业务对象。
/// 整数的跨语言精度约束由 [jsonInt] 在读取协议字段时单独检查。
Object? freezeJson(Object? value) {
  if (value is Map<String, Object?>) return jsonMap(value);
  if (value is List<Object?>)
    return List<Object?>.unmodifiable(value.map(freezeJson));
  if (value == null ||
      value is String ||
      value is bool ||
      value is int ||
      value is double && value.isFinite)
    return value;
  throw const FormatException('线路数据包含非 JSON 值。');
}

/// 只有缺失或 null 才使用显式默认值；错误类型不会被字符串化后继续传播。
String jsonString(JsonMap value, String key, {String? fallback}) {
  final item = value[key];
  if (item == null && fallback != null) return fallback;
  if (item is! String) throw FormatException('$key 必须是字符串。');
  return item;
}

/// 限制在 IEEE 754 可精确表示的整数范围，保持 Dart、JavaScript 与服务端的身份比较一致。
/// 不接受浮点形式的整数或数字字符串，避免静默修复破坏协议版本校验。
int jsonInt(JsonMap value, String key, {int? fallback}) {
  final item = value[key];
  if (item == null && fallback != null) return fallback;
  if (item is! int || item.abs() > 9007199254740991) {
    throw FormatException('$key 必须是可精确表示的整数。');
  }
  return item;
}

/// 布尔字段严格按类型读取；仅调用者明确允许缺省时才返回 [fallback]。
bool jsonBool(JsonMap value, String key, {bool? fallback}) {
  final item = value[key];
  if (item == null && fallback != null) return fallback;
  if (item is! bool) throw FormatException('$key 必须是布尔值。');
  return item;
}

/// 列表及每个对象均生成不可变副本；任一成员无效时整次解析失败，不丢弃坏项后返回残缺集合。
List<JsonMap> jsonObjects(Object? value) {
  if (value is! List<Object?>) throw const FormatException('字段必须是对象列表。');
  return List<JsonMap>.unmodifiable(value.map((item) => jsonMap(item)));
}

/// 属性顺序不属于协议语义；数组顺序保留。此编码仅用于比较，不替代服务端 profile hash。
String canonicalJson(Object? value) {
  Object? sorted(Object? item) {
    if (item is Map<String, Object?>) {
      final keys = item.keys.toList()..sort();
      return <String, Object?>{for (final key in keys) key: sorted(item[key])};
    }
    if (item is List<Object?>) return item.map(sorted).toList();
    return item;
  }

  return jsonEncode(sorted(freezeJson(value)));
}

/// 保留传输失败与业务拒绝的区分。possiblyApplied 表示请求已送出而结果未知，禁止自动重放。
class ApiException implements Exception {
  const ApiException(
    this.code,
    this.message, {
    this.statusCode,
    this.possiblyApplied = false,
  });
  final String code;
  final String message;
  final int? statusCode;
  final bool possiblyApplied;
  @override
  String toString() => message;
}

/// 统一校验具有 ok 的操作结果；业务查询的 pending/error 由专用 DTO 处理。
void requireOperationSuccess(JsonMap value) {
  if (value['ok'] == false ||
      value['error'] is String && (value['error'] as String).isNotEmpty) {
    throw ApiException(
      jsonString(value, 'code', fallback: 'business-rejected'),
      jsonString(value, 'error', fallback: '服务端拒绝了操作。'),
    );
  }
}
