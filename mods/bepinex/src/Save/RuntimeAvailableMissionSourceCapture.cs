using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace MystiaStewardCompanion.Save;

internal readonly record struct RuntimeAvailableMissionSourceStartToken(
    long Sequence)
{
    public bool IsActive => Sequence > 0;
}

internal static class RuntimeAvailableMissionSourceCapture
{
    private const string SchedulerTypeName =
        "GameData.RunTime.Common.RunTimeScheduler";
    private const string SchedulerNodeTypeName = "GameData.Profile.SchedulerNode";
    private const string EventNodeTypeName =
        "GameData.Profile.SchedulerNodeCollection.EventNode";
    private const string Il2CppStringArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray";
    private const int ExpectedHookCount = 4;
    private const int MaxMissionReferences = 4096;
    private const int MaxIdentityLength = 512;

    private static readonly object AttachRoot = new();
    private static readonly RuntimeAvailableMissionSourceState State = new();

    [ThreadStatic]
    private static Stack<FinishHookFrame>? _finishFrames;

    [ThreadStatic]
    private static Stack<StartHookFrame>? _startFrames;

    [ThreadStatic]
    private static long _nextStartSequence;

    private static Harmony? _harmony;
    private static ManualLogSource? _log;
    private static RuntimeShape? _shape;
    private static bool _hooksReady;

    public static RuntimeAvailableMissionSourceSnapshot Snapshot()
    {
        return State.Snapshot();
    }

    public static void Attach(ManualLogSource log)
    {
        ArgumentNullException.ThrowIfNull(log);
        lock (AttachRoot)
        {
            _log = log;
            if (_hooksReady) return;

            try
            {
                var shape = RuntimeShape.Resolve();
                var harmony = _harmony ??= new Harmony(
                    "com.tyukki.mystia-steward-companion.runtime-available-mission-source-capture");
                PatchSchedulerBoundary(
                    harmony,
                    shape.ScheduleEvent,
                    nameof(OnScheduleEventPrefix),
                    nameof(OnScheduleEventPostfix));
                PatchSchedulerBoundary(
                    harmony,
                    shape.DismissEvent,
                    nameof(OnDismissEventPrefix),
                    nameof(OnDismissEventPostfix));
                PatchFinishBoundary(
                    harmony,
                    shape.FinishSchedulerNode,
                    nameof(OnFinishSchedulerNodePrefix),
                    nameof(OnFinishSchedulerNodePostfix));
                PatchFinishBoundary(
                    harmony,
                    shape.FinishSchedulerNodePost,
                    nameof(OnFinishSchedulerNodePostPrefix),
                    nameof(OnFinishSchedulerNodePostPostfix));

                _shape = shape;
                _hooksReady = true;
                State.SetHookStatus(
                    attached: true,
                    $"patched:{ExpectedHookCount}/{ExpectedHookCount}",
                    DateTime.UtcNow);
                log.LogInfo(
                    "Runtime available mission source capture patched with exact scheduler boundaries.");
            }
            catch (Exception ex)
            {
                _shape = null;
                _hooksReady = false;
                State.SetHookStatus(
                    attached: false,
                    $"unavailable:{DescribeException(ex)}",
                    DateTime.UtcNow);
                log.LogWarning(
                    "Runtime available mission source capture unavailable; available tasks will fail closed: "
                    + ex.GetBaseException().Message);
            }
        }
    }

    public static void ResetForMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        try
        {
            _finishFrames?.Clear();
            _startFrames?.Clear();
            State.ResetForMissionGeneration(
                missionGeneration,
                ownerThreadId,
                changedAtUtc);
        }
        catch (Exception ex)
        {
            LogFailure("generation-reset", ex);
        }
    }

    public static void ArmMissionGeneration(
        long missionGeneration,
        int ownerThreadId,
        DateTime changedAtUtc)
    {
        try
        {
            if (!State.ArmMissionGeneration(
                    missionGeneration,
                    ownerThreadId,
                    changedAtUtc))
            {
                _log?.LogWarning(
                    $"Runtime available mission source generation {missionGeneration} could not be armed; available tasks remain fail-closed.");
            }
        }
        catch (Exception ex)
        {
            LogFailure("generation-arm", ex);
        }
    }

    public static RuntimeAvailableMissionSourceStartToken BeginMissionStart(
        string missionLabel)
    {
        try
        {
            if (!_hooksReady
                || _finishFrames is not { Count: > 0 } finishFrames)
            {
                return default;
            }

            RequireIdentity(missionLabel, "start-mission-label");
            var finishFrame = finishFrames.Peek();
            var nested = _startFrames is { Count: > 0 };
            var sequence = checked(++_nextStartSequence);
            var frame = new StartHookFrame(
                sequence,
                finishFrame,
                isDirect: !nested,
                sourceOrdinal: -1);
            if (!nested)
            {
                if (finishFrame.NextMissionOrdinal >= finishFrame.MissionLabels.Count)
                {
                    finishFrame.Failure = "unexpected-direct-start-mission";
                }
                else
                {
                    var ordinal = finishFrame.NextMissionOrdinal++;
                    frame.SourceOrdinal = ordinal;
                    if (!string.Equals(
                            finishFrame.MissionLabels[ordinal],
                            missionLabel,
                            StringComparison.Ordinal))
                    {
                        finishFrame.Failure =
                            $"direct-start-mission-order-mismatch:{ordinal}";
                    }
                }
            }

            (_startFrames ??= new Stack<StartHookFrame>()).Push(frame);
            return new RuntimeAvailableMissionSourceStartToken(sequence);
        }
        catch (Exception ex)
        {
            FailCurrentGeneration("start-prefix-correlation-failed", ex);
            return default;
        }
    }

    public static void CompleteMissionStart(
        RuntimeAvailableMissionSourceStartToken token,
        RuntimeAvailableMissionStartOutcome outcome)
    {
        if (!token.IsActive) return;

        try
        {
            if (_startFrames is not { Count: > 0 } frames)
            {
                throw new InvalidOperationException(
                    "source-start-frame-missing");
            }
            var frame = frames.Pop();
            if (frame.Sequence != token.Sequence)
            {
                frames.Clear();
                throw new InvalidOperationException(
                    "source-start-frame-order-mismatch");
            }
            if (!frame.IsDirect) return;
            if (frame.SourceOrdinal < 0
                || frame.SourceOrdinal >= frame.FinishFrame.Outcomes.Length)
            {
                frame.FinishFrame.Failure ??=
                    "source-start-ordinal-invalid";
                return;
            }
            if (frame.FinishFrame.Outcomes[frame.SourceOrdinal].HasValue)
            {
                frame.FinishFrame.Failure ??=
                    "source-start-outcome-duplicate";
                return;
            }
            frame.FinishFrame.Outcomes[frame.SourceOrdinal] = outcome;
            if (outcome == RuntimeAvailableMissionStartOutcome.Uncertain)
            {
                frame.FinishFrame.Failure ??=
                    $"source-start-outcome-uncertain:{frame.SourceOrdinal}";
            }
        }
        catch (Exception ex)
        {
            FailCurrentGeneration("start-postfix-correlation-failed", ex);
        }
    }

    public static void FailMissionGeneration(string reason)
    {
        try
        {
            var snapshot = State.Snapshot();
            State.FailMissionGeneration(
                snapshot.MissionGeneration,
                Environment.CurrentManagedThreadId,
                reason,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            LogFailure("mission-generation-fail", ex);
        }
    }

    private static void PatchSchedulerBoundary(
        Harmony harmony,
        MethodInfo target,
        string prefixName,
        string postfixName)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(prefixName))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(postfixName))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnBoundaryFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void PatchFinishBoundary(
        Harmony harmony,
        MethodInfo target,
        string prefixName,
        string postfixName)
    {
        harmony.Patch(
            target,
            prefix: new HarmonyMethod(RequireHook(prefixName))
            {
                priority = Priority.First,
            },
            postfix: new HarmonyMethod(RequireHook(postfixName))
            {
                priority = Priority.Last,
            },
            finalizer: new HarmonyMethod(RequireHook(nameof(OnFinishBoundaryFinalizer)))
            {
                priority = Priority.Last,
            });
    }

    private static void OnScheduleEventPrefix(
        object? __0,
        out SchedulerBoundaryHookFrame? __state)
    {
        __state = BeginSchedulerBoundary(__0, "schedule-event");
    }

    private static void OnScheduleEventPostfix(SchedulerBoundaryHookFrame? __state)
    {
        CompleteSchedulerBoundary(__state);
    }

    private static void OnDismissEventPrefix(
        object? __0,
        out SchedulerBoundaryHookFrame? __state)
    {
        __state = BeginSchedulerBoundary(__0, "dismiss-event");
    }

    private static void OnDismissEventPostfix(SchedulerBoundaryHookFrame? __state)
    {
        CompleteSchedulerBoundary(__state);
    }

    private static Exception? OnBoundaryFinalizer(
        Exception? __exception,
        SchedulerBoundaryHookFrame? __state)
    {
        try
        {
            if (__state != null && !__state.Completed)
            {
                if (__exception != null)
                {
                    State.FailMissionGeneration(
                        __state.Generation,
                        __state.ThreadId,
                        $"native-{__state.Boundary}-exception",
                        DateTime.UtcNow);
                }
                __state.Completed = true;
            }
        }
        catch (Exception ex)
        {
            LogFailure("scheduler-boundary-finalizer", ex);
        }
        return __exception;
    }

    private static void OnFinishSchedulerNodePrefix(
        object? __0,
        out FinishHookFrame? __state)
    {
        __state = BeginFinishBoundary(
            __0,
            RuntimeAvailableMissionSourceState.BeforePerformanceSource);
    }

    private static void OnFinishSchedulerNodePostfix(FinishHookFrame? __state)
    {
        CompleteFinishBoundary(__state);
    }

    private static void OnFinishSchedulerNodePostPrefix(
        object? __0,
        out FinishHookFrame? __state)
    {
        __state = BeginFinishBoundary(
            __0,
            RuntimeAvailableMissionSourceState.AfterPerformanceSource);
    }

    private static void OnFinishSchedulerNodePostPostfix(FinishHookFrame? __state)
    {
        CompleteFinishBoundary(__state);
    }

    private static Exception? OnFinishBoundaryFinalizer(
        Exception? __exception,
        FinishHookFrame? __state)
    {
        try
        {
            if (__state != null && !__state.Completed)
            {
                PopFinishFrame(__state);
                State.FailMissionGeneration(
                    __state.Generation,
                    __state.ThreadId,
                    __exception == null
                        ? "finish-scheduler-postfix-not-completed"
                        : "finish-scheduler-native-exception",
                    DateTime.UtcNow);
                __state.Completed = true;
            }
        }
        catch (Exception ex)
        {
            LogFailure("finish-boundary-finalizer", ex);
        }
        return __exception;
    }

    private static SchedulerBoundaryHookFrame? BeginSchedulerBoundary(
        object? rawLabel,
        string boundary)
    {
        try
        {
            if (!_hooksReady) return null;
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable) return null;
            if (rawLabel is not string label)
            {
                State.FailMissionGeneration(
                    snapshot.MissionGeneration,
                    Environment.CurrentManagedThreadId,
                    $"{boundary}-label-type-mismatch",
                    DateTime.UtcNow);
                return null;
            }
            RequireIdentity(label, $"{boundary}-label");
            return new SchedulerBoundaryHookFrame(
                snapshot.MissionGeneration,
                Environment.CurrentManagedThreadId,
                label,
                boundary);
        }
        catch (Exception ex)
        {
            FailCurrentGeneration($"{boundary}-prefix-failed", ex);
            return null;
        }
    }

    private static void CompleteSchedulerBoundary(
        SchedulerBoundaryHookFrame? frame)
    {
        if (frame == null || frame.Completed) return;
        try
        {
            State.ObserveSchedulerBoundary(
                frame.Generation,
                frame.ThreadId,
                frame.EventLabel,
                frame.Boundary,
                DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            State.FailMissionGeneration(
                frame.Generation,
                frame.ThreadId,
                $"{frame.Boundary}-postfix-failed",
                DateTime.UtcNow);
            LogFailure(frame.Boundary, ex);
        }
        finally
        {
            frame.Completed = true;
        }
    }

    private static FinishHookFrame? BeginFinishBoundary(
        object? node,
        string source)
    {
        try
        {
            if (!_hooksReady) return null;
            var snapshot = State.Snapshot();
            if (!snapshot.RuntimeAvailable) return null;
            var shape = _shape
                ?? throw new InvalidOperationException(
                    "available-mission-source-shape-missing");
            if (node == null || node.GetType() != shape.EventNodeType)
            {
                return null;
            }
            if (shape.Label.GetValue(node) is not string eventLabel)
            {
                throw new InvalidOperationException(
                    "finish-event-label-type-mismatch");
            }
            RequireIdentity(eventLabel, "finish-event-label");
            var beforeLabels = ReadMissionLabels(
                shape.PostMissions.GetValue(node),
                eventLabel,
                RuntimeAvailableMissionSourceState.BeforePerformanceSource);
            var afterLabels = ReadMissionLabels(
                shape.PostMissionsAfterPerformance.GetValue(node),
                eventLabel,
                RuntimeAvailableMissionSourceState.AfterPerformanceSource);
            var missionLabels = string.Equals(
                    source,
                    RuntimeAvailableMissionSourceState.BeforePerformanceSource,
                    StringComparison.Ordinal)
                ? beforeLabels
                : afterLabels;
            var frame = new FinishHookFrame(
                snapshot.MissionGeneration,
                Environment.CurrentManagedThreadId,
                eventLabel,
                source,
                missionLabels,
                afterLabels);
            (_finishFrames ??= new Stack<FinishHookFrame>()).Push(frame);
            return frame;
        }
        catch (Exception ex)
        {
            FailCurrentGeneration("finish-scheduler-prefix-failed", ex);
            return null;
        }
    }

    private static void CompleteFinishBoundary(FinishHookFrame? frame)
    {
        if (frame == null || frame.Completed) return;
        try
        {
            PopFinishFrame(frame);
            if (!string.IsNullOrEmpty(frame.Failure)
                || frame.NextMissionOrdinal != frame.MissionLabels.Count
                || frame.Outcomes.Any(outcome => !outcome.HasValue))
            {
                State.FailMissionGeneration(
                    frame.Generation,
                    frame.ThreadId,
                    frame.Failure
                        ?? "finish-scheduler-start-sequence-incomplete",
                    DateTime.UtcNow);
                return;
            }

            var outcomes = frame.Outcomes
                .Select(outcome => outcome!.Value)
                .ToArray();
            if (string.Equals(
                    frame.Source,
                    RuntimeAvailableMissionSourceState.BeforePerformanceSource,
                    StringComparison.Ordinal))
            {
                State.CommitBeforePerformance(
                    frame.Generation,
                    frame.ThreadId,
                    frame.EventLabel,
                    outcomes,
                    frame.AfterPerformanceMissionLabels,
                    DateTime.UtcNow);
            }
            else
            {
                State.CommitAfterPerformance(
                    frame.Generation,
                    frame.ThreadId,
                    frame.EventLabel,
                    frame.MissionLabels,
                    outcomes,
                    DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            State.FailMissionGeneration(
                frame.Generation,
                frame.ThreadId,
                "finish-scheduler-postfix-failed",
                DateTime.UtcNow);
            LogFailure("finish-scheduler-postfix", ex);
        }
        finally
        {
            frame.Completed = true;
        }
    }

    private static void PopFinishFrame(FinishHookFrame frame)
    {
        if (_finishFrames is not { Count: > 0 } frames
            || !ReferenceEquals(frames.Peek(), frame))
        {
            _finishFrames?.Clear();
            _startFrames?.Clear();
            throw new InvalidOperationException(
                "finish-scheduler-frame-order-mismatch");
        }
        frames.Pop();
        if (_startFrames is { Count: > 0 })
        {
            _startFrames.Clear();
            throw new InvalidOperationException(
                "finish-scheduler-start-frame-leaked");
        }
    }

    private static IReadOnlyList<string> ReadMissionLabels(
        object? rawArray,
        string eventLabel,
        string source)
    {
        if (!RuntimeConcreteCollectionReader.TryReadStringArray(
                rawArray,
                out var rawLabels,
                out var failure))
        {
            throw new InvalidOperationException(
                $"{source}-read-failed:{failure}");
        }
        if (rawLabels.Count > MaxMissionReferences)
        {
            throw new InvalidOperationException(
                $"{source}-overflow:{rawLabels.Count}");
        }

        var labels = new string[rawLabels.Count];
        for (var index = 0; index < rawLabels.Count; index++)
        {
            labels[index] = RuntimeScheduledEventDiagnosticIdentity.ReadNodeLabel(
                rawLabels[index],
                MaxIdentityLength,
                $"event-{eventLabel}-{source}",
                index);
        }
        return labels;
    }

    private static MethodInfo RequireHook(string name)
    {
        return typeof(RuntimeAvailableMissionSourceCapture).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(RuntimeAvailableMissionSourceCapture).FullName,
                name);
    }

    private static void FailCurrentGeneration(string stage, Exception exception)
    {
        try
        {
            var snapshot = State.Snapshot();
            State.FailMissionGeneration(
                snapshot.MissionGeneration,
                Environment.CurrentManagedThreadId,
                $"{stage}:{DescribeException(exception)}",
                DateTime.UtcNow);
        }
        catch
        {
        }
        LogFailure(stage, exception);
    }

    private static string DescribeException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: not null })
        {
            current = current.InnerException;
        }
        return $"{current.GetType().Name}:{current.Message}";
    }

    private static void LogFailure(string stage, Exception exception)
    {
        _log?.LogWarning(
            $"Runtime available mission source {stage} failed: "
            + exception.GetBaseException().Message);
    }

    private static void RequireIdentity(string identity, string source)
    {
        if (string.IsNullOrEmpty(identity)
            || identity.Length > MaxIdentityLength)
        {
            throw new InvalidOperationException($"{source}-invalid");
        }
    }

    private sealed class SchedulerBoundaryHookFrame
    {
        public SchedulerBoundaryHookFrame(
            long generation,
            int threadId,
            string eventLabel,
            string boundary)
        {
            Generation = generation;
            ThreadId = threadId;
            EventLabel = eventLabel;
            Boundary = boundary;
        }

        public long Generation { get; }
        public int ThreadId { get; }
        public string EventLabel { get; }
        public string Boundary { get; }
        public bool Completed { get; set; }
    }

    private sealed class FinishHookFrame
    {
        public FinishHookFrame(
            long generation,
            int threadId,
            string eventLabel,
            string source,
            IReadOnlyList<string> missionLabels,
            IReadOnlyList<string> afterPerformanceMissionLabels)
        {
            Generation = generation;
            ThreadId = threadId;
            EventLabel = eventLabel;
            Source = source;
            MissionLabels = missionLabels.ToArray();
            AfterPerformanceMissionLabels =
                afterPerformanceMissionLabels.ToArray();
            Outcomes = new RuntimeAvailableMissionStartOutcome?[MissionLabels.Count];
        }

        public long Generation { get; }
        public int ThreadId { get; }
        public string EventLabel { get; }
        public string Source { get; }
        public IReadOnlyList<string> MissionLabels { get; }
        public IReadOnlyList<string> AfterPerformanceMissionLabels { get; }
        public RuntimeAvailableMissionStartOutcome?[] Outcomes { get; }
        public int NextMissionOrdinal { get; set; }
        public string? Failure { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class StartHookFrame
    {
        public StartHookFrame(
            long sequence,
            FinishHookFrame finishFrame,
            bool isDirect,
            int sourceOrdinal)
        {
            Sequence = sequence;
            FinishFrame = finishFrame;
            IsDirect = isDirect;
            SourceOrdinal = sourceOrdinal;
        }

        public long Sequence { get; }
        public FinishHookFrame FinishFrame { get; }
        public bool IsDirect { get; }
        public int SourceOrdinal { get; set; }
    }

    private sealed record RuntimeShape(
        Type EventNodeType,
        MethodInfo ScheduleEvent,
        MethodInfo DismissEvent,
        MethodInfo FinishSchedulerNode,
        MethodInfo FinishSchedulerNodePost,
        PropertyInfo Label,
        PropertyInfo PostMissions,
        PropertyInfo PostMissionsAfterPerformance)
    {
        public static RuntimeShape Resolve()
        {
            var schedulerType = RequireType(SchedulerTypeName);
            var schedulerNodeType = RequireType(SchedulerNodeTypeName);
            var eventNodeType = RequireType(EventNodeTypeName);
            return new RuntimeShape(
                eventNodeType,
                RequireExactStaticMethod(
                    schedulerType,
                    "ScheduleEvent",
                    typeof(void),
                    typeof(string)),
                RequireExactStaticMethod(
                    schedulerType,
                    "DismissEvent",
                    typeof(void),
                    typeof(string)),
                RequireExactStaticMethod(
                    schedulerType,
                    "FinishSchedulerNode",
                    typeof(void),
                    schedulerNodeType),
                RequireExactStaticMethod(
                    schedulerType,
                    "FinishSchedulerNodePost",
                    typeof(void),
                    schedulerNodeType),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "label",
                    type => type == typeof(string)),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "postMissions",
                    IsExactStringArray),
                RequireExactInstanceProperty(
                    schedulerNodeType,
                    "postMissionsAfterPerformance",
                    IsExactStringArray));
        }

        private static Type RequireType(string fullName)
        {
            return RuntimeReflectionUtility.FindType(fullName)
                ?? throw new InvalidOperationException(
                    $"{fullName} is not loaded.");
        }

        private static MethodInfo RequireExactStaticMethod(
            Type declaringType,
            string methodName,
            Type returnType,
            params Type[] parameterTypes)
        {
            var matches = declaringType
                .GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(method =>
                    string.Equals(method.Name, methodName, StringComparison.Ordinal)
                    && !method.IsGenericMethod
                    && method.ReturnType == returnType
                    && method.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .SequenceEqual(parameterTypes))
                .Take(2)
                .ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new MissingMethodException(
                    declaringType.FullName,
                    methodName);
        }

        private static PropertyInfo RequireExactInstanceProperty(
            Type declaringType,
            string propertyName,
            Func<Type, bool> propertyType)
        {
            var property = declaringType.GetProperty(
                propertyName,
                BindingFlags.Public
                | BindingFlags.Instance
                | BindingFlags.DeclaredOnly);
            if (property == null
                || property.GetIndexParameters().Length != 0
                || !propertyType(property.PropertyType)
                || property.GetMethod is not { IsPublic: true, IsStatic: false })
            {
                throw new MissingMemberException(
                    declaringType.FullName,
                    propertyName);
            }
            return property;
        }

        private static bool IsExactStringArray(Type type)
        {
            return string.Equals(
                type.FullName,
                Il2CppStringArrayTypeName,
                StringComparison.Ordinal);
        }
    }
}
