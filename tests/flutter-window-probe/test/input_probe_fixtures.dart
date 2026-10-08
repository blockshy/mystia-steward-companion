import 'dart:convert';

import 'package:mystia_steward_companion_window_probe/generated/input_probe_api.g.dart';

const inputSha = '283bd56cd10564d64169a8ea521f9fdffe0019b4';

XInputSnapshot inputSample(
  int sequence,
  int time, {
  bool pressed = false,
  bool connected = true,
  bool focused = true,
  int packet = 0,
}) => XInputSnapshot(
  nativeGitSha: inputSha,
  processId: 100,
  sequence: sequence,
  monotonicMicros: time,
  systemDllPath: r'C:\Windows\System32\xinput1_4.dll',
  foregroundProcessId: focused ? 100 : 200,
  probeFocused: focused,
  slots: List.generate(
    4,
    (index) => XInputSlot(
      index: index,
      resultCode: connected && index == 0 ? 0 : 1167,
      state: connected && index == 0
          ? XInputState(
              packetNumber: packet,
              buttons: pressed ? 0x80 : 0,
              leftTrigger: 0,
              rightTrigger: 0,
              thumbLX: 0,
              thumbLY: 0,
              thumbRX: 0,
              thumbRY: 0,
            )
          : null,
    ),
  ),
);

FocusSnapshot focusSample(int sequence, int request) => FocusSnapshot(
  nativeGitSha: inputSha,
  processId: 100,
  sequence: sequence,
  requestId: request,
  requestPending: false,
  gamePid: 200,
  gameCreationTimeHex: '1abc',
  gameAlive: true,
  gameIdentityMatched: true,
  gameCloseRequested: false,
  gameHwnd: 20,
  gameWindowCount: 1,
  gameThreadId: 201,
  gameFocusHwnd: 20,
  probeHwnd: 10,
  probeChildHwnd: 11,
  probeThreadId: 101,
  probeVisible: true,
  inputMode: InputProbeMode.interactive,
  foregroundHwnd: 20,
  foregroundProcessId: 200,
  focusHwnd: null,
  focusOwnerPid: 0,
  probeMouseDown: 0,
  probeMouseUp: 0,
  probeKeyDown: 0,
  lastForegroundResult: false,
  lastForegroundError: 0,
  foregroundGrant: ForegroundGrantSnapshot(
    ready: true,
    identityMatched: true,
    grantSequence: 0,
    requestId: 0,
    responseSequence: 0,
    issuerProcessId: 200,
    targetProcessId: 100,
    foregroundHwnd: 0,
    foregroundProcessId: 0,
    foregroundAfterHwnd: 0,
    foregroundAfterProcessId: 0,
    activationRequested: false,
  ),
  diagnosticsJson: jsonEncode({'gameFocusOwned': true}),
);
