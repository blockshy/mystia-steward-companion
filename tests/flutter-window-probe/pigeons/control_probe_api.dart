import 'package:pigeon/pigeon.dart';

enum ControlOperation {
  initialize,
  inspect,
  focusGame,
  returnGame,
  hideProbe,
  setPassThrough,
  setInteractive,
  pressF8,
  releaseF8,
  clickProbe,
  sendFocusKey,
  closeGame,
}

class ControlCommand {
  ControlCommand({
    required this.operation,
    required this.requestId,
    required this.flutterFocused,
    required this.dartF8DownCount,
  });
  ControlOperation operation;
  int requestId;
  bool flutterFocused;
  int dartF8DownCount;
}

class ControlSnapshot {
  ControlSnapshot({
    required this.gitSha,
    required this.processId,
    required this.sequence,
    required this.evidenceJson,
  });
  String gitSha;
  int processId;
  int sequence;
  String evidenceJson;
}

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/control_probe_api.g.dart',
    cppHeaderOut: 'windows/runner/control_probe_api.g.h',
    cppSourceOut: 'windows/runner/control_probe_api.g.cpp',
    cppOptions: CppOptions(namespace: 'mystia_control_probe'),
  ),
)
@HostApi()
abstract class ControlProbeHostApi {
  @async
  ControlSnapshot execute(ControlCommand command);

  @async
  void finish(String reportJson, int exitCode);
}
