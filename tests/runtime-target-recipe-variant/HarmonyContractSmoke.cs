using System.Runtime.CompilerServices;
using HarmonyLib;

internal static class HarmonyContractSmoke
{
    private const string HarmonyId =
        "mystia-steward-companion.tests.runtime-target-recipe-variant.harmony-contract";

    internal static void Verify()
    {
        var harmony = new Harmony(HarmonyId);

        try
        {
            harmony.Patch(
                RequireMethod(
                    typeof(ArgumentArraySelectionTarget),
                    nameof(ArgumentArraySelectionTarget.OnRecipeElementSelected)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(RecipeSelectionPatch),
                    nameof(RecipeSelectionPatch.ObjectArrayPrefix)))
                {
                    priority = Priority.First,
                });

            var exactDefinition = RequireMethod(
                typeof(RecipeSelectionPatch),
                nameof(RecipeSelectionPatch.ExactPrefix));
            harmony.Patch(
                RequireMethod(
                    typeof(RecipeSelectionTarget),
                    nameof(RecipeSelectionTarget.OnRecipeElementSelected)),
                prefix: new HarmonyMethod(exactDefinition.MakeGenericMethod(typeof(RecipeProbe)))
                {
                    priority = Priority.First,
                });

            var statefulExactDefinition = RequireMethod(
                typeof(StatefulRecipeSelectionPatch),
                nameof(StatefulRecipeSelectionPatch.ExactPrefix));
            harmony.Patch(
                RequireMethod(
                    typeof(StatefulRecipeSelectionTarget),
                    nameof(StatefulRecipeSelectionTarget.OnRecipeElementSelected)),
                prefix: new HarmonyMethod(
                    statefulExactDefinition.MakeGenericMethod(typeof(RecipeProbe)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(StatefulRecipeSelectionPatch),
                    nameof(StatefulRecipeSelectionPatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(SubmitTarget),
                    nameof(SubmitTarget.CallSubmitAction)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(SubmitLeasePatch),
                    nameof(SubmitLeasePatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(SubmitLeasePatch),
                    nameof(SubmitLeasePatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(NestedOutputClosureTarget),
                    nameof(NestedOutputClosureTarget.OutputSubmitClosure)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(NestedOutputClosurePatch),
                    nameof(NestedOutputClosurePatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(NestedOutputClosurePatch),
                    nameof(NestedOutputClosurePatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(NestedPanelCloseTarget),
                    nameof(NestedPanelCloseTarget.OnPanelClose)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(NestedPanelClosePatch),
                    nameof(NestedPanelClosePatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(NestedPanelClosePatch),
                    nameof(NestedPanelClosePatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(SuppressedOriginalTarget),
                    nameof(SuppressedOriginalTarget.Invoke)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(SuppressedOriginalPatch),
                    nameof(SuppressedOriginalPatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(SuppressedOriginalPatch),
                    nameof(SuppressedOriginalPatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(PrefixFailureTarget),
                    nameof(PrefixFailureTarget.Invoke)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(PrefixFailurePatch),
                    nameof(PrefixFailurePatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(PrefixFailurePatch),
                    nameof(PrefixFailurePatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            harmony.Patch(
                RequireMethod(
                    typeof(OutputSelectionTarget),
                    nameof(OutputSelectionTarget.OnOutputSelected)),
                prefix: new HarmonyMethod(RequireMethod(
                    typeof(OutputSelectionFinalizerPatch),
                    nameof(OutputSelectionFinalizerPatch.Prefix)))
                {
                    priority = Priority.First,
                },
                finalizer: new HarmonyMethod(RequireMethod(
                    typeof(OutputSelectionFinalizerPatch),
                    nameof(OutputSelectionFinalizerPatch.Finalizer)))
                {
                    priority = Priority.Last,
                });

            VerifyArgumentArrayDoesNotWriteBack();
            VerifyClosedGenericExactReplacement();
            VerifyClosedGenericStateCanInjectBlockingException();
            VerifyClosedGenericStatePreservesOriginalException();
            VerifySubmitLeaseOnNormalReturn();
            VerifySubmitLeaseOnExceptionalReturn();
            VerifyNestedOutputCloseOnNormalReturn();
            VerifyNestedOutputCloseExceptionIdentity();
            VerifyNestedOutputExceptionIdentity();
            VerifySkippedOriginalStillFinalizesLease();
            VerifyPrefixFailureStillFinalizesAssignedState();
            VerifyOutputSelectionPostNativeCleanup();
            VerifyOutputSelectionCleanupFailureAbortsOuterSubmit();
            VerifyOutputSelectionNativeExceptionPreservesUnknownCallback();
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }

    private static void VerifyArgumentArrayDoesNotWriteBack()
    {
        var originalRecipe = new RecipeProbe("original");
        var replacementRecipe = new RecipeProbe("replacement");
        var cluster = new ClusterProbe("cluster");
        var button = new ButtonProbe("button");
        var target = new ArgumentArraySelectionTarget();

        RecipeSelectionPatch.ResetObjectArray(replacementRecipe);
        target.OnRecipeElementSelected(originalRecipe, cluster, button);

        AssertEqual(1, RecipeSelectionPatch.PrefixCalls,
            "The recipe-selection prefix was not called exactly once.");
        AssertSame(originalRecipe, RecipeSelectionPatch.PrefixSawOriginal,
            "The recipe-selection prefix did not receive the original first argument.");
        AssertEqual(1, target.CallCount,
            "The recipe-selection original was not called exactly once.");
        AssertSame(originalRecipe, target.SeenRecipe,
            "HarmonyX unexpectedly changed its object[] __args non-writeback contract.");
        AssertSame(cluster, target.SeenCluster,
            "Replacing __args[0] changed the second original argument.");
        AssertSame(button, target.SeenButton,
            "Replacing __args[0] changed the third original argument.");
    }

    private static void VerifyClosedGenericExactReplacement()
    {
        var originalRecipe = new RecipeProbe("original-exact");
        var replacementRecipe = new RecipeProbe("replacement-exact");
        var cluster = new ClusterProbe("cluster-exact");
        var button = new ButtonProbe("button-exact");
        var target = new RecipeSelectionTarget();

        RecipeSelectionPatch.ResetExact(replacementRecipe);
        target.OnRecipeElementSelected(originalRecipe, cluster, button);

        AssertEqual(1, RecipeSelectionPatch.ExactPrefixCalls,
            "The closed generic exact prefix was not called exactly once.");
        AssertSame(originalRecipe, RecipeSelectionPatch.ExactPrefixSawOriginal,
            "The closed generic exact prefix did not receive the original Recipe.");
        AssertSame(replacementRecipe, target.SeenRecipe,
            "The closed generic ref Recipe prefix did not write the replacement into the original call.");
        AssertSame(cluster, target.SeenCluster,
            "Exact recipe replacement changed the cluster argument.");
        AssertSame(button, target.SeenButton,
            "Exact recipe replacement changed the button argument.");
    }

    private static void VerifyClosedGenericStateCanInjectBlockingException()
    {
        var originalRecipe = new RecipeProbe("original-stateful-success");
        var replacementRecipe = new RecipeProbe("replacement-stateful-success");
        var cluster = new ClusterProbe("cluster-stateful-success");
        var button = new ButtonProbe("button-stateful-success");
        var target = new StatefulRecipeSelectionTarget(throwOnCall: false);

        StatefulRecipeSelectionPatch.Reset(replacementRecipe);
        Exception? observedException = null;

        try
        {
            target.OnRecipeElementSelected(originalRecipe, cluster, button);
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(StatefulRecipeSelectionPatch.BlockingException, observedException,
            "The non-generic recipe-selection finalizer did not inject its blocking exception.");
        AssertStatefulRecipeSelectionContract(
            target,
            originalRecipe,
            replacementRecipe,
            expectedOriginalException: null);
    }

    private static void VerifyClosedGenericStatePreservesOriginalException()
    {
        var originalRecipe = new RecipeProbe("original-stateful-failure");
        var replacementRecipe = new RecipeProbe("replacement-stateful-failure");
        var cluster = new ClusterProbe("cluster-stateful-failure");
        var button = new ButtonProbe("button-stateful-failure");
        var target = new StatefulRecipeSelectionTarget(throwOnCall: true);

        StatefulRecipeSelectionPatch.Reset(replacementRecipe);
        Exception? observedException = null;

        try
        {
            target.OnRecipeElementSelected(originalRecipe, cluster, button);
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(StatefulRecipeSelectionTarget.ExpectedException, observedException,
            "The non-generic recipe-selection finalizer replaced the original exception.");
        AssertStatefulRecipeSelectionContract(
            target,
            originalRecipe,
            replacementRecipe,
            StatefulRecipeSelectionTarget.ExpectedException);
    }

    private static void AssertStatefulRecipeSelectionContract(
        StatefulRecipeSelectionTarget target,
        RecipeProbe originalRecipe,
        RecipeProbe replacementRecipe,
        Exception? expectedOriginalException)
    {
        AssertEqual(1, target.CallCount,
            "The stateful recipe-selection original was not called exactly once.");
        AssertSame(replacementRecipe, target.SeenRecipe,
            "The stateful closed generic ref Recipe prefix did not replace the original argument.");
        AssertEqual(1, StatefulRecipeSelectionPatch.PrefixCalls,
            "The stateful recipe-selection prefix was not called exactly once.");
        AssertEqual(1, StatefulRecipeSelectionPatch.FinalizerCalls,
            "The stateful recipe-selection finalizer was not called exactly once.");
        AssertEqual(true, StatefulRecipeSelectionPatch.FinalizerSawExactState,
            "The non-generic finalizer did not receive the closed generic prefix's exact __state.");
        AssertSame(originalRecipe, StatefulRecipeSelectionPatch.LastPrefixState?.OriginalRecipe,
            "The shared recipe-selection state did not retain the original Recipe identity.");
        AssertSame(replacementRecipe, StatefulRecipeSelectionPatch.LastPrefixState?.ReplacementRecipe,
            "The shared recipe-selection state did not retain the replacement Recipe identity.");
        AssertSame(expectedOriginalException, StatefulRecipeSelectionPatch.LastOriginalException,
            "The non-generic finalizer received the wrong original exception.");
    }

    private static void VerifySubmitLeaseOnNormalReturn()
    {
        SubmitLeasePatch.ResetInvocation();
        var target = new SubmitTarget(throwOnCall: false);

        target.CallSubmitAction();

        AssertSubmitLeaseContract(target, expectedException: null);
    }

    private static void VerifySubmitLeaseOnExceptionalReturn()
    {
        SubmitLeasePatch.ResetInvocation();
        var target = new SubmitTarget(throwOnCall: true);
        Exception? observedException = null;

        try
        {
            target.CallSubmitAction();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(SubmitTarget.ExpectedException, observedException,
            "The submit finalizer swallowed or replaced the original exception.");
        AssertSubmitLeaseContract(target, SubmitTarget.ExpectedException);
    }

    private static void AssertSubmitLeaseContract(
        SubmitTarget target,
        Exception? expectedException)
    {
        AssertEqual(1, target.CallCount,
            "The submit original was not called exactly once.");
        AssertEqual(true, target.SawLeaseDuringOriginal,
            "The submit original did not observe the prefix-acquired thread lease.");
        AssertSame(SubmitLeasePatch.LastPrefixState, target.SeenLease,
            "The submit original did not observe the prefix's exact lease token.");
        AssertEqual(1, SubmitLeasePatch.PrefixCalls,
            "The submit prefix was not called exactly once.");
        AssertEqual(1, SubmitLeasePatch.FinalizerCalls,
            "The submit finalizer was not called exactly once.");
        AssertEqual(1, SubmitLeasePatch.ReleaseCalls,
            "The submit finalizer did not release the thread lease exactly once.");
        AssertEqual(true, SubmitLeasePatch.FinalizerSawExactState,
            "The submit finalizer did not receive the prefix's exact __state token.");
        AssertEqual(true, SubmitLeasePatch.FinalizerRanOnLeaseThread,
            "The submit finalizer did not run on the thread that acquired the lease.");
        AssertSame(expectedException, SubmitLeasePatch.LastException,
            "The submit finalizer did not receive the expected original exception.");
        AssertEqual(false, SubmitLeasePatch.IsLeaseActive,
            "The submit finalizer left the thread lease active.");
    }

    private static void VerifyNestedOutputCloseOnNormalReturn()
    {
        ResetNestedInvocation();
        var panel = new NestedPanelCloseTarget(throwOnClose: false);
        var output = new NestedOutputClosureTarget(panel, throwAfterClose: false);

        output.OutputSubmitClosure();

        AssertNestedOutputContract(
            output,
            panel,
            expectedException: null,
            expectedCloseException: null,
            expectedSynchronousReceipt: true);
        AssertSequence(
            "output-prefix",
            "output-original-enter",
            "close-prefix",
            "close-original-enter",
            "close-original-exit",
            "close-finalizer",
            "output-original-after-close",
            "output-original-exit",
            "output-finalizer");
    }

    private static void VerifyNestedOutputCloseExceptionIdentity()
    {
        ResetNestedInvocation();
        var panel = new NestedPanelCloseTarget(throwOnClose: true);
        var output = new NestedOutputClosureTarget(panel, throwAfterClose: false);
        Exception? observedException = null;

        try
        {
            output.OutputSubmitClosure();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(NestedPanelCloseTarget.ExpectedException, observedException,
            "The nested panel-close exception was swallowed or replaced.");
        AssertNestedOutputContract(
            output,
            panel,
            NestedPanelCloseTarget.ExpectedException,
            NestedPanelCloseTarget.ExpectedException,
            expectedSynchronousReceipt: false);
        AssertSequence(
            "output-prefix",
            "output-original-enter",
            "close-prefix",
            "close-original-enter",
            "close-original-throw",
            "close-finalizer",
            "output-finalizer");
    }

    private static void VerifyNestedOutputExceptionIdentity()
    {
        ResetNestedInvocation();
        var panel = new NestedPanelCloseTarget(throwOnClose: false);
        var output = new NestedOutputClosureTarget(panel, throwAfterClose: true);
        Exception? observedException = null;

        try
        {
            output.OutputSubmitClosure();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(NestedOutputClosureTarget.ExpectedException, observedException,
            "The output-closure exception was swallowed or replaced.");
        AssertNestedOutputContract(
            output,
            panel,
            NestedOutputClosureTarget.ExpectedException,
            expectedCloseException: null,
            expectedSynchronousReceipt: true);
        AssertSequence(
            "output-prefix",
            "output-original-enter",
            "close-prefix",
            "close-original-enter",
            "close-original-exit",
            "close-finalizer",
            "output-original-after-close",
            "output-original-throw",
            "output-finalizer");
    }

    private static void VerifySkippedOriginalStillFinalizesLease()
    {
        SuppressedOriginalPatch.ResetInvocation();
        var target = new SuppressedOriginalTarget();

        target.Invoke();

        AssertEqual(0, target.CallCount,
            "A false-returning prefix did not suppress the original method.");
        AssertEqual(1, SuppressedOriginalPatch.PrefixCalls,
            "The false-returning prefix was not called exactly once.");
        AssertEqual(1, SuppressedOriginalPatch.FinalizerCalls,
            "Harmony did not run the finalizer after the prefix returned false.");
        AssertEqual(true, SuppressedOriginalPatch.FinalizerSawExactState,
            "The skipped-original finalizer did not receive the prefix's exact state.");
        AssertEqual(true, SuppressedOriginalPatch.FinalizerSawNullException,
            "The skipped-original finalizer received an unexpected exception.");
        AssertEqual(1, SuppressedOriginalPatch.DisposeCalls,
            "The skipped-original finalizer did not dispose the lease exactly once.");
        AssertEqual(false, SuppressedOriginalPatch.IsLeaseActive,
            "The skipped-original finalizer left the thread lease active.");
        AssertEqual(true, SuppressedOriginalPatch.LastPrefixState?.Lease.IsDisposed == true,
            "The skipped-original finalizer did not dispose the exact prefix lease.");
    }

    private static void VerifyPrefixFailureStillFinalizesAssignedState()
    {
        PrefixFailurePatch.ResetInvocation();
        var target = new PrefixFailureTarget();
        Exception? observedException = null;

        try
        {
            target.Invoke();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(PrefixFailurePatch.ExpectedException, observedException,
            "Harmony swallowed or replaced an exception raised after prefix state assignment.");
        AssertEqual(0, target.CallCount,
            "The original ran after its prefix raised an exception.");
        AssertEqual(1, PrefixFailurePatch.PrefixCalls,
            "The failing prefix was not called exactly once.");
        AssertEqual(1, PrefixFailurePatch.FinalizerCalls,
            "Harmony did not run the finalizer after the prefix raised an exception.");
        AssertEqual(true, PrefixFailurePatch.FinalizerSawAssignedState,
            "The finalizer did not receive state assigned before the prefix exception.");
        AssertSame(PrefixFailurePatch.ExpectedException, PrefixFailurePatch.LastException,
            "The prefix failure finalizer received the wrong exception.");
    }

    private static void VerifyOutputSelectionPostNativeCleanup()
    {
        OutputSelectionFinalizerPatch.Reset(cleanupFails: false);
        var selection = new OutputSelectionTarget(throwBeforeOwnership: false);
        var submit = new OutputSubmitTarget(selection);

        submit.CallSubmitAction();

        AssertEqual(1, submit.CallCount,
            "The outer output submit did not run exactly once.");
        AssertEqual(1, selection.CallCount,
            "The native output-selection original did not run exactly once.");
        AssertEqual(0, selection.CallbackCalls,
            "The outer submit executed a callback that the finalizer suppressed.");
        AssertEqual(true, selection.Callback == null,
            "The normal-return finalizer did not clean the native-installed callback.");
        AssertEqual(1, OutputSelectionFinalizerPatch.CleanupCalls,
            "The normal-return finalizer did not clean exactly once.");
    }

    private static void VerifyOutputSelectionCleanupFailureAbortsOuterSubmit()
    {
        OutputSelectionFinalizerPatch.Reset(cleanupFails: true);
        var selection = new OutputSelectionTarget(throwBeforeOwnership: false);
        var submit = new OutputSubmitTarget(selection);
        Exception? observedException = null;

        try
        {
            submit.CallSubmitAction();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(OutputSelectionFinalizerPatch.BlockingException, observedException,
            "The output-selection finalizer did not inject its cleanup failure.");
        AssertEqual(0, selection.CallbackCalls,
            "The outer submit read the callback after finalizer cleanup failure.");
        AssertEqual(true, selection.Callback != null,
            "The cleanup-failure probe unexpectedly removed the callback.");
        AssertEqual(1, OutputSelectionFinalizerPatch.CleanupCalls,
            "The cleanup-failure finalizer did not attempt cleanup exactly once.");
    }

    private static void VerifyOutputSelectionNativeExceptionPreservesUnknownCallback()
    {
        OutputSelectionFinalizerPatch.Reset(cleanupFails: false);
        var selection = new OutputSelectionTarget(throwBeforeOwnership: true);
        selection.InstallPriorCallback();
        var prior = selection.Callback;
        var submit = new OutputSubmitTarget(selection);
        Exception? observedException = null;

        try
        {
            submit.CallSubmitAction();
        }
        catch (Exception ex)
        {
            observedException = ex;
        }

        AssertSame(OutputSelectionTarget.ExpectedException, observedException,
            "The output-selection finalizer replaced the native exception.");
        AssertSame(prior, selection.Callback,
            "The output-selection finalizer touched an unknown callback after native exception.");
        AssertEqual(0, selection.CallbackCalls,
            "The outer submit executed the prior callback after native exception.");
        AssertEqual(0, OutputSelectionFinalizerPatch.CleanupCalls,
            "The finalizer cleaned an unknown callback after native exception.");
    }

    private static void ResetNestedInvocation()
    {
        NestedCallTrace.Reset();
        NestedPanelClosePatch.ResetInvocation();
        NestedOutputClosurePatch.ResetInvocation();
    }

    private static void AssertNestedOutputContract(
        NestedOutputClosureTarget output,
        NestedPanelCloseTarget panel,
        Exception? expectedException,
        Exception? expectedCloseException,
        bool expectedSynchronousReceipt)
    {
        AssertEqual(1, output.CallCount,
            "The output-closure original was not called exactly once.");
        AssertEqual(1, panel.CallCount,
            "The nested panel-close original was not called exactly once.");
        AssertEqual(1, NestedOutputClosurePatch.PrefixCalls,
            "The output-closure prefix was not called exactly once.");
        AssertEqual(1, NestedOutputClosurePatch.FinalizerCalls,
            "The output-closure finalizer was not called exactly once.");
        AssertEqual(1, NestedPanelClosePatch.PrefixCalls,
            "The panel-close prefix was not called exactly once.");
        AssertEqual(1, NestedPanelClosePatch.FinalizerCalls,
            "The panel-close finalizer was not called exactly once.");
        AssertEqual(true, NestedPanelClosePatch.PrefixSawActiveOutputLease,
            "The panel-close prefix did not run synchronously inside the output lease.");
        AssertEqual(true, NestedPanelClosePatch.FinalizerSawExactState,
            "The panel-close finalizer did not receive the exact prefix state.");
        AssertEqual(true, NestedPanelClosePatch.FinalizerRanUnderOutputLease,
            "The panel-close finalizer ran after the output lease was released.");
        AssertEqual(true, NestedOutputClosurePatch.FinalizerSawExactState,
            "The output-closure finalizer did not receive the exact prefix state.");
        AssertEqual(expectedSynchronousReceipt,
            NestedOutputClosurePatch.FinalizerSawSynchronousCloseReceipt,
            "The output-closure finalizer observed the wrong synchronous close receipt.");
        AssertSame(expectedCloseException, NestedPanelClosePatch.LastException,
            "The panel-close finalizer swallowed or replaced the original exception.");
        AssertSame(expectedException, NestedOutputClosurePatch.LastException,
            "The output-closure finalizer swallowed or replaced the original exception.");
        AssertEqual(1, NestedOutputClosurePatch.ReleaseCalls,
            "The output-closure finalizer did not release its lease exactly once.");
        AssertEqual(false, NestedOutputClosurePatch.IsLeaseActive,
            "The output-closure finalizer left its thread lease active.");
    }

    private static void AssertSequence(params string[] expected)
    {
        var actual = NestedCallTrace.Events;
        AssertEqual(expected.Length, actual.Count,
            "The nested output/panel-close call count changed.");

        for (var index = 0; index < expected.Length; index++)
        {
            AssertEqual(expected[index], actual[index],
                $"The nested output/panel-close order changed at index {index}.");
        }
    }

    private static System.Reflection.MethodInfo RequireMethod(Type type, string name)
    {
        return AccessTools.Method(type, name)
            ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void AssertSame(object? expected, object? actual, string message)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException(
                $"{message} actual={actual}; expected={expected}.");
        }
    }

    private sealed class RecipeProbe
    {
        internal RecipeProbe(string id)
        {
            Id = id;
        }

        internal string Id { get; }
    }

    private sealed class ClusterProbe
    {
        internal ClusterProbe(string id)
        {
            Id = id;
        }

        internal string Id { get; }
    }

    private sealed class ButtonProbe
    {
        internal ButtonProbe(string id)
        {
            Id = id;
        }

        internal string Id { get; }
    }

    private sealed class RecipeSelectionTarget
    {
        internal int CallCount { get; private set; }
        internal RecipeProbe? SeenRecipe { get; private set; }
        internal ClusterProbe? SeenCluster { get; private set; }
        internal ButtonProbe? SeenButton { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OnRecipeElementSelected(
            RecipeProbe recipe,
            ClusterProbe cluster,
            ButtonProbe button)
        {
            CallCount++;
            SeenRecipe = recipe;
            SeenCluster = cluster;
            SeenButton = button;
        }
    }

    private sealed class ArgumentArraySelectionTarget
    {
        internal int CallCount { get; private set; }
        internal RecipeProbe? SeenRecipe { get; private set; }
        internal ClusterProbe? SeenCluster { get; private set; }
        internal ButtonProbe? SeenButton { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OnRecipeElementSelected(
            RecipeProbe recipe,
            ClusterProbe cluster,
            ButtonProbe button)
        {
            CallCount++;
            SeenRecipe = recipe;
            SeenCluster = cluster;
            SeenButton = button;
        }
    }

    private sealed class StatefulRecipeSelectionTarget
    {
        internal static readonly Exception ExpectedException =
            new RecipeSelectionProbeException("expected recipe-selection failure");

        private readonly bool _throwOnCall;

        internal StatefulRecipeSelectionTarget(bool throwOnCall)
        {
            _throwOnCall = throwOnCall;
        }

        internal int CallCount { get; private set; }
        internal RecipeProbe? SeenRecipe { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OnRecipeElementSelected(
            RecipeProbe recipe,
            ClusterProbe cluster,
            ButtonProbe button)
        {
            CallCount++;
            SeenRecipe = recipe;

            if (_throwOnCall)
            {
                throw ExpectedException;
            }
        }
    }

    private sealed class RecipeSelectionProbeException : Exception
    {
        internal RecipeSelectionProbeException(string message)
            : base(message)
        {
        }
    }

    private sealed class RecipeSelectionHookStateProbe
    {
        internal RecipeSelectionHookStateProbe(
            RecipeProbe originalRecipe,
            RecipeProbe replacementRecipe)
        {
            OriginalRecipe = originalRecipe;
            ReplacementRecipe = replacementRecipe;
        }

        internal RecipeProbe OriginalRecipe { get; }
        internal RecipeProbe ReplacementRecipe { get; }
    }

    private static class StatefulRecipeSelectionPatch
    {
        internal static readonly Exception BlockingException =
            new RecipeSelectionProbeException("blocked by recipe-selection finalizer");

        private static RecipeProbe? _replacement;

        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static bool FinalizerSawExactState { get; private set; }
        internal static RecipeSelectionHookStateProbe? LastPrefixState { get; private set; }
        internal static Exception? LastOriginalException { get; private set; }

        internal static void Reset(RecipeProbe replacement)
        {
            _replacement = replacement;
            PrefixCalls = 0;
            FinalizerCalls = 0;
            FinalizerSawExactState = false;
            LastPrefixState = null;
            LastOriginalException = null;
        }

        internal static bool ExactPrefix<TRecipe>(
            ref TRecipe __0,
            out RecipeSelectionHookStateProbe __state)
            where TRecipe : class
        {
            var original = __0 as RecipeProbe
                ?? throw new InvalidOperationException(
                    "The stateful recipe-selection prefix received the wrong Recipe type.");
            var replacement = _replacement
                ?? throw new InvalidOperationException(
                    "The stateful recipe-selection replacement was not initialized.");

            __state = new RecipeSelectionHookStateProbe(original, replacement);
            LastPrefixState = __state;
            PrefixCalls++;
            __0 = (TRecipe)(object)replacement;
            return true;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            RecipeSelectionHookStateProbe __state)
        {
            FinalizerCalls++;
            FinalizerSawExactState = ReferenceEquals(LastPrefixState, __state);
            LastOriginalException = __exception;
            return __exception ?? BlockingException;
        }
    }

    private static class RecipeSelectionPatch
    {
        private static RecipeProbe? _replacement;

        internal static int PrefixCalls { get; private set; }
        internal static object? PrefixSawOriginal { get; private set; }

        internal static int ExactPrefixCalls { get; private set; }
        internal static object? ExactPrefixSawOriginal { get; private set; }

        internal static void ResetObjectArray(RecipeProbe replacement)
        {
            _replacement = replacement;
            PrefixCalls = 0;
            PrefixSawOriginal = null;
        }

        internal static void ResetExact(RecipeProbe replacement)
        {
            _replacement = replacement;
            ExactPrefixCalls = 0;
            ExactPrefixSawOriginal = null;
        }

        internal static void ObjectArrayPrefix(object[] __args)
        {
            PrefixCalls++;

            if (__args.Length != 3)
            {
                throw new InvalidOperationException(
                    $"The recipe-selection prefix received {__args.Length} arguments instead of 3.");
            }

            PrefixSawOriginal = __args[0];
            __args[0] = _replacement
                ?? throw new InvalidOperationException(
                    "The recipe-selection replacement was not initialized.");
        }

        internal static void ExactPrefix<TRecipe>(ref TRecipe __0)
            where TRecipe : class
        {
            ExactPrefixCalls++;
            ExactPrefixSawOriginal = __0;
            __0 = (TRecipe)(object)(_replacement
                ?? throw new InvalidOperationException(
                    "The exact recipe-selection replacement was not initialized."));
        }
    }

    private sealed class OutputSubmitTarget
    {
        private readonly OutputSelectionTarget _selection;

        internal OutputSubmitTarget(OutputSelectionTarget selection)
        {
            _selection = selection;
        }

        internal int CallCount { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void CallSubmitAction()
        {
            CallCount++;
            _selection.OnOutputSelected();
            _selection.Callback?.Invoke();
        }
    }

    private sealed class OutputSelectionTarget
    {
        internal static readonly Exception ExpectedException =
            new OutputSelectionProbeException("expected native output-selection failure");

        private readonly bool _throwBeforeOwnership;

        internal OutputSelectionTarget(bool throwBeforeOwnership)
        {
            _throwBeforeOwnership = throwBeforeOwnership;
        }

        internal int CallCount { get; private set; }
        internal int CallbackCalls { get; private set; }
        internal Action? Callback { get; set; }

        internal void InstallPriorCallback()
        {
            Callback = () => CallbackCalls++;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OnOutputSelected()
        {
            CallCount++;
            if (_throwBeforeOwnership)
            {
                throw ExpectedException;
            }
            Callback = () => CallbackCalls++;
        }
    }

    private sealed class OutputSelectionProbeException : Exception
    {
        internal OutputSelectionProbeException(string message)
            : base(message)
        {
        }
    }

    private sealed class OutputSelectionHookStateProbe
    {
    }

    private static class OutputSelectionFinalizerPatch
    {
        internal static readonly Exception BlockingException =
            new OutputSelectionProbeException("blocked after output callback cleanup failure");

        private static bool _cleanupFails;

        internal static int CleanupCalls { get; private set; }

        internal static void Reset(bool cleanupFails)
        {
            _cleanupFails = cleanupFails;
            CleanupCalls = 0;
        }

        internal static bool Prefix(out OutputSelectionHookStateProbe __state)
        {
            __state = new OutputSelectionHookStateProbe();
            return true;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            OutputSelectionTarget __instance,
            OutputSelectionHookStateProbe __state)
        {
            _ = __state;
            if (__exception != null) return __exception;

            CleanupCalls++;
            if (_cleanupFails) return BlockingException;
            __instance.Callback = null;
            return null;
        }
    }

    private sealed class SubmitTarget
    {
        internal static readonly Exception ExpectedException =
            new SubmitProbeException("expected submit failure");

        private readonly bool _throwOnCall;

        internal SubmitTarget(bool throwOnCall)
        {
            _throwOnCall = throwOnCall;
        }

        internal int CallCount { get; private set; }
        internal bool SawLeaseDuringOriginal { get; private set; }
        internal SubmitLeaseToken? SeenLease { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void CallSubmitAction()
        {
            CallCount++;
            SawLeaseDuringOriginal = SubmitLeasePatch.IsLeaseActive;
            SeenLease = SubmitLeasePatch.CurrentLease;

            if (_throwOnCall)
            {
                throw ExpectedException;
            }
        }
    }

    private sealed class SubmitProbeException : Exception
    {
        internal SubmitProbeException(string message)
            : base(message)
        {
        }
    }

    private sealed class SubmitLeaseToken
    {
        internal SubmitLeaseToken(int threadId, long sequence)
        {
            ThreadId = threadId;
            Sequence = sequence;
        }

        internal int ThreadId { get; }
        internal long Sequence { get; }
    }

    private static class SubmitLeasePatch
    {
        [ThreadStatic]
        private static SubmitLeaseToken? _activeLease;

        private static long _sequence;

        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static int ReleaseCalls { get; private set; }
        internal static bool FinalizerSawExactState { get; private set; }
        internal static bool FinalizerRanOnLeaseThread { get; private set; }
        internal static SubmitLeaseToken? LastPrefixState { get; private set; }
        internal static Exception? LastException { get; private set; }
        internal static bool IsLeaseActive => _activeLease != null;
        internal static SubmitLeaseToken? CurrentLease => _activeLease;

        internal static void ResetInvocation()
        {
            _activeLease = null;
            PrefixCalls = 0;
            FinalizerCalls = 0;
            ReleaseCalls = 0;
            FinalizerSawExactState = false;
            FinalizerRanOnLeaseThread = false;
            LastPrefixState = null;
            LastException = null;
        }

        internal static void Prefix(out SubmitLeaseToken __state)
        {
            if (_activeLease != null)
            {
                throw new InvalidOperationException(
                    "The submit prefix tried to acquire an already-held thread lease.");
            }

            __state = new SubmitLeaseToken(
                Environment.CurrentManagedThreadId,
                Interlocked.Increment(ref _sequence));
            _activeLease = __state;
            LastPrefixState = __state;
            PrefixCalls++;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            SubmitLeaseToken __state)
        {
            FinalizerCalls++;
            LastException = __exception;
            FinalizerSawExactState = ReferenceEquals(_activeLease, __state);
            FinalizerRanOnLeaseThread =
                __state.ThreadId == Environment.CurrentManagedThreadId;

            if (_activeLease != null)
            {
                _activeLease = null;
                ReleaseCalls++;
            }

            return __exception;
        }
    }

    private static class NestedCallTrace
    {
        private static readonly List<string> Timeline = new();

        internal static IReadOnlyList<string> Events => Timeline;

        internal static void Reset()
        {
            Timeline.Clear();
        }

        internal static void Record(string value)
        {
            Timeline.Add(value);
        }
    }

    private sealed class NestedPanelCloseTarget
    {
        internal static readonly Exception ExpectedException =
            new PanelCloseProbeException("expected panel-close failure");

        private readonly bool _throwOnClose;

        internal NestedPanelCloseTarget(bool throwOnClose)
        {
            _throwOnClose = throwOnClose;
        }

        internal int CallCount { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OnPanelClose()
        {
            CallCount++;
            NestedCallTrace.Record("close-original-enter");
            if (_throwOnClose)
            {
                NestedCallTrace.Record("close-original-throw");
                throw ExpectedException;
            }

            NestedCallTrace.Record("close-original-exit");
        }
    }

    private sealed class NestedOutputClosureTarget
    {
        internal static readonly Exception ExpectedException =
            new OutputClosureProbeException("expected output-closure failure");

        private readonly NestedPanelCloseTarget _panel;
        private readonly bool _throwAfterClose;

        internal NestedOutputClosureTarget(
            NestedPanelCloseTarget panel,
            bool throwAfterClose)
        {
            _panel = panel;
            _throwAfterClose = throwAfterClose;
        }

        internal int CallCount { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void OutputSubmitClosure()
        {
            CallCount++;
            NestedCallTrace.Record("output-original-enter");
            _panel.OnPanelClose();
            NestedCallTrace.Record("output-original-after-close");
            if (_throwAfterClose)
            {
                NestedCallTrace.Record("output-original-throw");
                throw ExpectedException;
            }

            NestedCallTrace.Record("output-original-exit");
        }
    }

    private sealed class PanelCloseProbeException : Exception
    {
        internal PanelCloseProbeException(string message)
            : base(message)
        {
        }
    }

    private sealed class OutputClosureProbeException : Exception
    {
        internal OutputClosureProbeException(string message)
            : base(message)
        {
        }
    }

    private sealed class NestedOutputLease
    {
        internal NestedOutputLease(int threadId, long sequence)
        {
            ThreadId = threadId;
            Sequence = sequence;
        }

        internal int ThreadId { get; }
        internal long Sequence { get; }
    }

    private sealed class NestedPanelCloseState
    {
        internal NestedPanelCloseState(NestedOutputLease outputLease)
        {
            OutputLease = outputLease;
        }

        internal NestedOutputLease OutputLease { get; }
    }

    private static class NestedOutputClosurePatch
    {
        [ThreadStatic]
        private static NestedOutputLease? _activeLease;

        private static long _sequence;

        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static int ReleaseCalls { get; private set; }
        internal static bool FinalizerSawExactState { get; private set; }
        internal static bool FinalizerSawSynchronousCloseReceipt { get; private set; }
        internal static NestedOutputLease? LastPrefixState { get; private set; }
        internal static Exception? LastException { get; private set; }
        internal static bool IsLeaseActive => _activeLease != null;
        internal static NestedOutputLease? CurrentLease => _activeLease;

        internal static void ResetInvocation()
        {
            _activeLease = null;
            PrefixCalls = 0;
            FinalizerCalls = 0;
            ReleaseCalls = 0;
            FinalizerSawExactState = false;
            FinalizerSawSynchronousCloseReceipt = false;
            LastPrefixState = null;
            LastException = null;
        }

        internal static bool Prefix(out NestedOutputLease __state)
        {
            if (_activeLease != null)
            {
                throw new InvalidOperationException(
                    "The output-closure prefix tried to acquire an active lease.");
            }

            __state = new NestedOutputLease(
                Environment.CurrentManagedThreadId,
                Interlocked.Increment(ref _sequence));
            _activeLease = __state;
            LastPrefixState = __state;
            PrefixCalls++;
            NestedCallTrace.Record("output-prefix");
            return true;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            NestedOutputLease __state)
        {
            FinalizerCalls++;
            LastException = __exception;
            FinalizerSawExactState = ReferenceEquals(_activeLease, __state);
            FinalizerSawSynchronousCloseReceipt =
                NestedPanelClosePatch.SuccessfulReceiptSequence == __state.Sequence;
            NestedCallTrace.Record("output-finalizer");

            if (ReferenceEquals(_activeLease, __state))
            {
                _activeLease = null;
                ReleaseCalls++;
            }

            return __exception;
        }
    }

    private static class NestedPanelClosePatch
    {
        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static bool PrefixSawActiveOutputLease { get; private set; }
        internal static bool FinalizerSawExactState { get; private set; }
        internal static bool FinalizerRanUnderOutputLease { get; private set; }
        internal static long SuccessfulReceiptSequence { get; private set; }
        internal static NestedPanelCloseState? LastPrefixState { get; private set; }
        internal static Exception? LastException { get; private set; }

        internal static void ResetInvocation()
        {
            PrefixCalls = 0;
            FinalizerCalls = 0;
            PrefixSawActiveOutputLease = false;
            FinalizerSawExactState = false;
            FinalizerRanUnderOutputLease = false;
            SuccessfulReceiptSequence = 0;
            LastPrefixState = null;
            LastException = null;
        }

        internal static void Prefix(out NestedPanelCloseState __state)
        {
            var outputLease = NestedOutputClosurePatch.CurrentLease
                ?? throw new InvalidOperationException(
                    "The panel-close prefix ran without an active output lease.");
            __state = new NestedPanelCloseState(outputLease);
            LastPrefixState = __state;
            PrefixSawActiveOutputLease = true;
            PrefixCalls++;
            NestedCallTrace.Record("close-prefix");
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            NestedPanelCloseState __state)
        {
            FinalizerCalls++;
            LastException = __exception;
            FinalizerSawExactState = ReferenceEquals(LastPrefixState, __state);
            FinalizerRanUnderOutputLease = ReferenceEquals(
                NestedOutputClosurePatch.CurrentLease,
                __state.OutputLease);
            if (__exception == null)
            {
                SuccessfulReceiptSequence = __state.OutputLease.Sequence;
            }

            NestedCallTrace.Record("close-finalizer");
            return __exception;
        }
    }

    private sealed class SuppressedOriginalTarget
    {
        internal int CallCount { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void Invoke()
        {
            CallCount++;
        }
    }

    private sealed class PrefixFailureTarget
    {
        internal int CallCount { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal void Invoke()
        {
            CallCount++;
        }
    }

    private sealed class PrefixFailureToken
    {
    }

    private static class PrefixFailurePatch
    {
        internal static readonly Exception ExpectedException =
            new InvalidOperationException("expected prefix failure");

        private static PrefixFailureToken? _assignedState;

        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static bool FinalizerSawAssignedState { get; private set; }
        internal static Exception? LastException { get; private set; }

        internal static void ResetInvocation()
        {
            _assignedState = null;
            PrefixCalls = 0;
            FinalizerCalls = 0;
            FinalizerSawAssignedState = false;
            LastException = null;
        }

        internal static void Prefix(out PrefixFailureToken __state)
        {
            __state = new PrefixFailureToken();
            _assignedState = __state;
            PrefixCalls++;
            throw ExpectedException;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            PrefixFailureToken __state)
        {
            FinalizerCalls++;
            LastException = __exception;
            FinalizerSawAssignedState = ReferenceEquals(_assignedState, __state);
            return __exception;
        }
    }

    private sealed class SuppressedOriginalLease
    {
        internal bool IsDisposed { get; private set; }

        internal void Dispose()
        {
            if (IsDisposed)
            {
                throw new InvalidOperationException(
                    "The skipped-original lease was disposed more than once.");
            }

            IsDisposed = true;
        }
    }

    private sealed class SuppressedOriginalState
    {
        internal SuppressedOriginalState(SuppressedOriginalLease lease)
        {
            Lease = lease;
        }

        internal SuppressedOriginalLease Lease { get; }
    }

    private static class SuppressedOriginalPatch
    {
        [ThreadStatic]
        private static SuppressedOriginalLease? _activeLease;

        internal static int PrefixCalls { get; private set; }
        internal static int FinalizerCalls { get; private set; }
        internal static int DisposeCalls { get; private set; }
        internal static bool FinalizerSawExactState { get; private set; }
        internal static bool FinalizerSawNullException { get; private set; }
        internal static SuppressedOriginalState? LastPrefixState { get; private set; }
        internal static bool IsLeaseActive => _activeLease != null;

        internal static void ResetInvocation()
        {
            _activeLease = null;
            PrefixCalls = 0;
            FinalizerCalls = 0;
            DisposeCalls = 0;
            FinalizerSawExactState = false;
            FinalizerSawNullException = false;
            LastPrefixState = null;
        }

        internal static bool Prefix(out SuppressedOriginalState __state)
        {
            if (_activeLease != null)
            {
                throw new InvalidOperationException(
                    "The false-returning prefix tried to acquire an active lease.");
            }

            _activeLease = new SuppressedOriginalLease();
            __state = new SuppressedOriginalState(_activeLease);
            LastPrefixState = __state;
            PrefixCalls++;
            return false;
        }

        internal static Exception? Finalizer(
            Exception? __exception,
            SuppressedOriginalState __state)
        {
            FinalizerCalls++;
            FinalizerSawExactState = ReferenceEquals(LastPrefixState, __state);
            FinalizerSawNullException = __exception == null;

            if (ReferenceEquals(_activeLease, __state.Lease))
            {
                _activeLease.Dispose();
                _activeLease = null;
                DisposeCalls++;
            }

            return __exception;
        }
    }
}
