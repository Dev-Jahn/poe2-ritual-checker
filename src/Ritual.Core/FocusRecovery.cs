namespace Ritual.Core;

public sealed class FocusRecovery
{
    public bool RequiresValidation { get; private set; }

    public void LostFocus() => RequiresValidation = true;

    public void FrameValidated() => RequiresValidation = false;

    public bool CanDisplay(bool foreground) => foreground && !RequiresValidation;
}
