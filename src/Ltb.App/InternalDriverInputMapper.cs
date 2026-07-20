using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.App;

/// <summary>
/// Maps one complete public LibOVR Touch sample onto the first-party driver
/// input contract. Trigger-click state is derived from the analog trigger with
/// an independent hysteresis latch for each hand because LibOVR exposes no
/// physical trigger-click bit.
/// </summary>
internal sealed class InternalDriverInputMapper
{
    internal const float TriggerPressThreshold = 0.55f;
    internal const float TriggerReleaseThreshold = 0.45f;

    private bool _leftTriggerPressed;
    private bool _rightTriggerPressed;

    public ProtocolInputState Map(MetaLinkControllerSnapshot controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (!controller.Analog.IsValid)
        {
            throw new ArgumentException(
                "Touch analog state must be finite and within the public LibOVR ranges.",
                nameof(controller));
        }

        var triggerPressed = UpdateTriggerLatch(
            controller.Hand,
            controller.Analog.IndexTrigger);
        var buttons = controller.Hand switch
        {
            MetaLinkHand.Left => MapLeftButtons(controller.Buttons, triggerPressed),
            MetaLinkHand.Right => MapRightButtons(controller.Buttons, triggerPressed),
            _ => throw new ArgumentOutOfRangeException(nameof(controller)),
        };
        var touches = controller.Hand switch
        {
            MetaLinkHand.Left => MapLeftTouches(controller.Touches),
            MetaLinkHand.Right => MapRightTouches(controller.Touches),
            _ => throw new ArgumentOutOfRangeException(nameof(controller)),
        };

        return new ProtocolInputState(
            buttons,
            touches,
            controller.Analog.IndexTrigger,
            controller.Analog.GripTrigger,
            controller.Analog.Thumbstick.X,
            controller.Analog.Thumbstick.Y);
    }

    /// <summary>Clears derived state for one hand when that hand is neutralized.</summary>
    public void Neutralize(MetaLinkHand hand)
    {
        switch (hand)
        {
            case MetaLinkHand.Left:
                _leftTriggerPressed = false;
                break;
            case MetaLinkHand.Right:
                _rightTriggerPressed = false;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(hand));
        }
    }

    /// <summary>Clears both latches for a new runtime or feed session.</summary>
    public void Reset()
    {
        _leftTriggerPressed = false;
        _rightTriggerPressed = false;
    }

    private bool UpdateTriggerLatch(MetaLinkHand hand, float trigger)
    {
        return hand switch
        {
            MetaLinkHand.Left => _leftTriggerPressed = NextTriggerState(
                _leftTriggerPressed,
                trigger),
            MetaLinkHand.Right => _rightTriggerPressed = NextTriggerState(
                _rightTriggerPressed,
                trigger),
            _ => throw new ArgumentOutOfRangeException(nameof(hand)),
        };
    }

    private static bool NextTriggerState(bool wasPressed, float trigger) =>
        wasPressed
            ? trigger > TriggerReleaseThreshold
            : trigger >= TriggerPressThreshold;

    private static ProtocolButtons MapLeftButtons(
        MetaLinkButtons buttons,
        bool triggerPressed)
    {
        var mapped = ProtocolButtons.None;
        Add(ref mapped, buttons.X, ProtocolButtons.Primary);
        Add(ref mapped, buttons.Y, ProtocolButtons.Secondary);
        Add(ref mapped, buttons.Menu, ProtocolButtons.Menu);
        Add(ref mapped, buttons.Thumbstick, ProtocolButtons.ThumbstickClick);
        Add(ref mapped, triggerPressed, ProtocolButtons.TriggerClick);
        return mapped;
    }

    private static ProtocolButtons MapRightButtons(
        MetaLinkButtons buttons,
        bool triggerPressed)
    {
        var mapped = ProtocolButtons.None;
        Add(ref mapped, buttons.A, ProtocolButtons.Primary);
        Add(ref mapped, buttons.B, ProtocolButtons.Secondary);
        Add(ref mapped, buttons.Thumbstick, ProtocolButtons.ThumbstickClick);
        Add(ref mapped, triggerPressed, ProtocolButtons.TriggerClick);
        return mapped;
    }

    private static ProtocolTouches MapLeftTouches(MetaLinkTouches touches)
    {
        var mapped = ProtocolTouches.None;
        Add(ref mapped, touches.X, ProtocolTouches.Primary);
        Add(ref mapped, touches.Y, ProtocolTouches.Secondary);
        Add(ref mapped, touches.IndexTrigger, ProtocolTouches.Trigger);
        Add(ref mapped, touches.Thumbstick, ProtocolTouches.Thumbstick);
        Add(ref mapped, touches.ThumbRest, ProtocolTouches.ThumbRest);
        Add(ref mapped, touches.IndexPointing, ProtocolTouches.IndexPointing);
        Add(ref mapped, touches.ThumbUp, ProtocolTouches.ThumbUp);
        return mapped;
    }

    private static ProtocolTouches MapRightTouches(MetaLinkTouches touches)
    {
        var mapped = ProtocolTouches.None;
        Add(ref mapped, touches.A, ProtocolTouches.Primary);
        Add(ref mapped, touches.B, ProtocolTouches.Secondary);
        Add(ref mapped, touches.IndexTrigger, ProtocolTouches.Trigger);
        Add(ref mapped, touches.Thumbstick, ProtocolTouches.Thumbstick);
        Add(ref mapped, touches.ThumbRest, ProtocolTouches.ThumbRest);
        Add(ref mapped, touches.IndexPointing, ProtocolTouches.IndexPointing);
        Add(ref mapped, touches.ThumbUp, ProtocolTouches.ThumbUp);
        return mapped;
    }

    private static void Add(
        ref ProtocolButtons destination,
        bool present,
        ProtocolButtons value)
    {
        if (present)
        {
            destination |= value;
        }
    }

    private static void Add(
        ref ProtocolTouches destination,
        bool present,
        ProtocolTouches value)
    {
        if (present)
        {
            destination |= value;
        }
    }
}
