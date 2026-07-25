using System.Numerics;
using Ltb.App;
using Ltb.Core;
using Ltb.MetaLink;
using Ltb.Protocol;

namespace Ltb.Integration.Tests;

public sealed class InternalDriverInputMapperTests
{
    [Fact]
    public void MapsEverySupportedLeftTouchInputExactly()
    {
        var mapper = new InternalDriverInputMapper();
        var controller = Controller(
            MetaLinkHand.Left,
            new MetaLinkButtons(
                A: true,
                B: true,
                X: true,
                Y: true,
                Thumbstick: true,
                Menu: true,
                RawMask: uint.MaxValue),
            new MetaLinkTouches(
                A: true,
                B: true,
                X: true,
                Y: true,
                Thumbstick: true,
                ThumbRest: true,
                IndexTrigger: true,
                IndexPointing: true,
                ThumbUp: true,
                RawMask: uint.MaxValue),
            trigger: 0.8f,
            grip: 0.6f,
            stick: new Vector2(-0.25f, 0.75f));

        var mapped = mapper.Map(controller);

        Assert.Equal(
            ProtocolButtons.Primary |
            ProtocolButtons.Secondary |
            ProtocolButtons.Menu |
            ProtocolButtons.ThumbstickClick |
            ProtocolButtons.TriggerClick,
            mapped.Buttons);
        Assert.Equal(ProtocolConstants.AllowedTouches, mapped.Touches);
        Assert.Equal(0.8f, mapped.Trigger);
        Assert.Equal(0.6f, mapped.Grip);
        Assert.Equal(-0.25f, mapped.StickX);
        Assert.Equal(0.75f, mapped.StickY);
    }

    [Fact]
    public void MapsEverySupportedRightTouchInputExactlyAndNeverMapsMenu()
    {
        var mapper = new InternalDriverInputMapper();
        var controller = Controller(
            MetaLinkHand.Right,
            new MetaLinkButtons(
                A: true,
                B: true,
                X: true,
                Y: true,
                Thumbstick: true,
                Menu: true,
                RawMask: uint.MaxValue),
            new MetaLinkTouches(
                A: true,
                B: true,
                X: true,
                Y: true,
                Thumbstick: true,
                ThumbRest: true,
                IndexTrigger: true,
                IndexPointing: true,
                ThumbUp: true,
                RawMask: uint.MaxValue),
            trigger: 0.7f,
            grip: 0.4f,
            stick: new Vector2(0.5f, -0.75f));

        var mapped = mapper.Map(controller);

        Assert.Equal(
            ProtocolButtons.Primary |
            ProtocolButtons.Secondary |
            ProtocolButtons.ThumbstickClick |
            ProtocolButtons.TriggerClick,
            mapped.Buttons);
        Assert.Equal(ProtocolConstants.AllowedTouches, mapped.Touches);
        Assert.False(mapped.Buttons.HasFlag(ProtocolButtons.Menu));
        Assert.Equal(0.7f, mapped.Trigger);
        Assert.Equal(0.4f, mapped.Grip);
        Assert.Equal(0.5f, mapped.StickX);
        Assert.Equal(-0.75f, mapped.StickY);
    }

    [Fact]
    public void DerivesTriggerClickWithIndependentPerHandHysteresis()
    {
        var mapper = new InternalDriverInputMapper();

        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.54f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.55f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.50f));
        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.50f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.90f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.4501f));
        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.45f));
        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.45f));
    }

    [Fact]
    public void NeutralizeAndResetClearOnlyTheIntendedTriggerLatches()
    {
        var mapper = new InternalDriverInputMapper();
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Left, 1f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Right, 1f));

        mapper.Neutralize(MetaLinkHand.Left);

        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.50f));
        Assert.True(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.50f));

        mapper.Reset();

        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Left, 0.50f));
        Assert.False(IsTriggerPressed(mapper, MetaLinkHand.Right, 0.50f));
    }

    [Fact]
    public void RejectsInvalidAnalogInputInsteadOfPublishingIt()
    {
        var mapper = new InternalDriverInputMapper();
        var invalid = Controller(
            MetaLinkHand.Left,
            default,
            default,
            trigger: float.NaN,
            grip: 0f,
            stick: Vector2.Zero);

        var exception = Assert.Throws<ArgumentException>(() => mapper.Map(invalid));

        Assert.Equal("controller", exception.ParamName);
    }

    private static bool IsTriggerPressed(
        InternalDriverInputMapper mapper,
        MetaLinkHand hand,
        float trigger) =>
        mapper.Map(Controller(
                hand,
                default,
                default,
                trigger,
                grip: 0f,
                stick: Vector2.Zero))
            .Buttons
            .HasFlag(ProtocolButtons.TriggerClick);

    private static MetaLinkControllerSnapshot Controller(
        MetaLinkHand hand,
        MetaLinkButtons buttons,
        MetaLinkTouches touches,
        float trigger,
        float grip,
        Vector2 stick) =>
        new(
            hand,
            new MetaLinkPoseSnapshot(
                RigidTransform.Identity,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.Zero,
                Vector3.Zero,
                isOrientationTracked: true,
                isPositionTracked: true,
                hasValidOrientation: true,
                hasValidPosition: true,
                rawMetaTimeSeconds: 41d,
                appMonotonicTimeSeconds: 7d,
                appMonotonicTimeNanoseconds: 7_000_000_000,
                clockUncertaintySeconds: 0.001d),
            buttons,
            touches,
            new MetaLinkAnalogState(trigger, grip, stick),
            MetaLinkBatteryState.Unavailable);
}
