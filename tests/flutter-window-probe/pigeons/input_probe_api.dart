import 'package:pigeon/pigeon.dart';

enum InputProbeMode { interactive, passThrough }

enum FocusOperation {
  initialize,
  inspect,
  focusGame,
  focusProbe,
  setPassThrough,
  hideProbe,
  clickProbe,
  sendFocusKey,
  closeGame,
}

class XInputState {
  XInputState({
    required this.packetNumber,
    required this.buttons,
    required this.leftTrigger,
    required this.rightTrigger,
    required this.thumbLX,
    required this.thumbLY,
    required this.thumbRX,
    required this.thumbRY,
  });
  int packetNumber;
  int buttons;
  int leftTrigger;
  int rightTrigger;
  int thumbLX;
  int thumbLY;
  int thumbRX;
  int thumbRY;
}

class XInputSlot {
  XInputSlot({required this.index, required this.resultCode, this.state});
  int index;
  int resultCode;
  XInputState? state;
}

class XInputSnapshot {
  XInputSnapshot({
    required this.nativeGitSha,
    required this.processId,
    required this.sequence,
    required this.monotonicMicros,
    required this.systemDllPath,
    required this.foregroundProcessId,
    required this.probeFocused,
    required this.slots,
  });
  String nativeGitSha;
  int processId;
  int sequence;
  int monotonicMicros;
  String systemDllPath;
  int foregroundProcessId;
  bool probeFocused;
  List<XInputSlot> slots;
}

class FocusCommand {
  FocusCommand({required this.operation, required this.requestId});
  FocusOperation operation;
  int requestId;
}

class ForegroundGrantSnapshot {
  ForegroundGrantSnapshot({
    required this.ready,
    required this.identityMatched,
    required this.grantSequence,
    required this.requestId,
    required this.responseSequence,
    required this.issuerProcessId,
    required this.targetProcessId,
    this.allowResult,
    this.allowError,
    required this.foregroundHwnd,
    required this.foregroundProcessId,
    required this.foregroundAfterHwnd,
    required this.foregroundAfterProcessId,
    required this.activationRequested,
  });
  bool ready;
  bool identityMatched;
  int grantSequence;
  int requestId;
  int responseSequence;
  int issuerProcessId;
  int targetProcessId;
  bool? allowResult;
  int? allowError;
  int foregroundHwnd;
  int foregroundProcessId;
  int foregroundAfterHwnd;
  int foregroundAfterProcessId;
  bool activationRequested;
}

class FocusSnapshot {
  FocusSnapshot({
    required this.nativeGitSha,
    required this.processId,
    required this.sequence,
    required this.requestId,
    required this.requestPending,
    required this.gamePid,
    required this.gameCreationTimeHex,
    required this.gameAlive,
    required this.gameIdentityMatched,
    this.gameExitCode,
    required this.gameCloseRequested,
    this.gameHwnd,
    required this.gameWindowCount,
    required this.gameThreadId,
    this.gameFocusHwnd,
    required this.probeHwnd,
    required this.probeChildHwnd,
    required this.probeThreadId,
    required this.probeVisible,
    required this.inputMode,
    required this.foregroundHwnd,
    required this.foregroundProcessId,
    this.focusHwnd,
    required this.focusOwnerPid,
    required this.probeMouseDown,
    required this.probeMouseUp,
    required this.probeKeyDown,
    required this.lastForegroundResult,
    required this.lastForegroundError,
    this.foregroundGrant,
    required this.diagnosticsJson,
  });
  String nativeGitSha;
  int processId;
  int sequence;
  int requestId;
  bool requestPending;
  int gamePid;
  String gameCreationTimeHex;
  bool gameAlive;
  bool gameIdentityMatched;
  int? gameExitCode;
  bool gameCloseRequested;
  int? gameHwnd;
  int gameWindowCount;
  int gameThreadId;
  int? gameFocusHwnd;
  int probeHwnd;
  int probeChildHwnd;
  int probeThreadId;
  bool probeVisible;
  InputProbeMode inputMode;
  int foregroundHwnd;
  int foregroundProcessId;
  // This is always the probe thread's focus, independent of foreground owner.
  int? focusHwnd;
  int focusOwnerPid;
  int probeMouseDown;
  int probeMouseUp;
  int probeKeyDown;
  bool lastForegroundResult;
  int lastForegroundError;
  ForegroundGrantSnapshot? foregroundGrant;
  String diagnosticsJson;
}

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/input_probe_api.g.dart',
    cppHeaderOut: 'windows/runner/input_probe_api.g.h',
    cppSourceOut: 'windows/runner/input_probe_api.g.cpp',
    cppOptions: CppOptions(namespace: 'mystia_input_probe'),
  ),
)
@HostApi()
abstract class InputProbeHostApi {
  @async
  XInputSnapshot sampleXInput();

  @async
  FocusSnapshot executeFocus(FocusCommand command);

  // Native binds the fixed result path at launch and rejects cross-suite calls.
  @async
  void finish(String reportJson, int exitCode);
}
