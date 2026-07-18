using MystiaStewardCompanion.Ui;

try
{
    VerifyPressIsLatchedUntilRelease();
    VerifyHeldInputDoesNotRearmAfterTime();
    VerifyHeldObservationWithoutEdgeStillLatches();
    Console.WriteLine("PASS: controller toggle edges latch until release and never depend on elapsed time.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifyPressIsLatchedUntilRelease()
{
    var state = new ControllerToggleState();
    AssertEqual(true, state.Update(held: true, pressedThisFrame: true), "Initial edge was not accepted.");
    AssertEqual(false, state.Update(held: true, pressedThisFrame: false), "Held input repeated without an edge.");
    AssertEqual(false, state.Update(held: true, pressedThisFrame: true), "A duplicate edge repeated before release.");
    AssertEqual(false, state.Update(held: false, pressedThisFrame: false), "Release triggered an action.");
    AssertEqual(true, state.Update(held: true, pressedThisFrame: true), "A new edge after release was not accepted.");
}

static void VerifyHeldInputDoesNotRearmAfterTime()
{
    var state = new ControllerToggleState();
    AssertEqual(true, state.Update(true, true), "Initial edge was not accepted.");
    for (var frame = 0; frame < 10_000; frame += 1)
    {
        AssertEqual(false, state.Update(true, frame % 120 == 0), "Held input rearmed without release.");
    }
}

static void VerifyHeldObservationWithoutEdgeStillLatches()
{
    var state = new ControllerToggleState();
    AssertEqual(false, state.Update(false, false), "Neutral input triggered an action.");
    AssertEqual(false, state.Update(true, false), "Held input without a press edge triggered an action.");
    AssertEqual(false, state.Update(true, true), "A delayed edge triggered before physical release.");
    AssertEqual(false, state.Update(false, false), "Release triggered an action.");
    AssertEqual(true, state.Update(true, true), "A fresh edge after physical release was not accepted.");
    AssertEqual(false, state.Update(false, true), "An inconsistent press edge without held state triggered an action.");
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
