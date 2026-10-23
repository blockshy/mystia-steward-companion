/// 可接取任务的非就绪响应仍是完整只读快照。C# 的 error 在这种响应中承载
/// 运行时诊断，并不表示 HTTP 命令执行失败；与写入操作的 ok/error 信封不同。
/// 本适配只识别当前 LocalApiAvailableMissionsDto 的非就绪形状，不通用于其它资源。
class AvailableMissionReadState {
  const AvailableMissionReadState._(this.status, this.diagnostic);

  final String status;
  final String diagnostic;

  static const _statuses = {
    'not-attached',
    'waiting-for-load',
    'loading',
    'runtime-unavailable',
    'mission-data-incomplete',
  };
  static final _signature = RegExp(r'^[0-9a-f]{64}$');

  /// 只有完整、明确成功取得且无任务条目的非就绪快照才允许保留诊断。
  /// ok:false、ready+error、缺字段、未来状态或带任务的矛盾响应仍走普通失败校验；
  /// 未变化信封也不走本分支，避免把缺失内容当成一份新的空快照。
  static AvailableMissionReadState? tryRead(Map<String, Object?> value) {
    final status = value['status'];
    final error = value['error'];
    final missions = value['missions'];
    final generation = value['missionGeneration'];
    final revision = value['sourceRevision'];
    final signature = value['contentSignature'];
    if (value['ok'] != true ||
        value['runtimeAvailable'] != false ||
        value['unchanged'] != false ||
        status is! String ||
        !_statuses.contains(status) ||
        error is! String ||
        missions is! List ||
        missions.isNotEmpty ||
        value['availableCount'] is! int ||
        value['availableCount'] != 0 ||
        generation is! int ||
        generation < 0 ||
        revision is! int ||
        revision < 0 ||
        signature is! String ||
        !_signature.hasMatch(signature)) {
      return null;
    }
    return AvailableMissionReadState._(status, error);
  }

  /// 只折叠 C# 明确表示“尚未就绪”的正常等待诊断，不隐藏读取异常或任意 error。
  /// 原始诊断仍完整留在模型资源中；页面以中文等待状态替代这些技术性重复信息。
  bool get hasRoutineWaitDiagnostic =>
      (status == 'waiting-for-load' || status == 'loading') &&
      (diagnostic.isEmpty ||
          diagnostic.startsWith('mission-runtime-not-ready:') ||
          diagnostic == 'mapped-guest-runtime-not-ready');
}
