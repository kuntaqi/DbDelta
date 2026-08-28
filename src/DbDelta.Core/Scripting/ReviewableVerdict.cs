namespace DbDelta.Core.Scripting;

public enum ReviewableVerdict
{
    // Even the high estimate fits.
    Within,

    // The high estimate does not fit and the low one does. Which it turns out to be depends on how many
    // rows actually differ, and nothing short of comparing them knows that.
    Possibly,

    // Even the low estimate does not fit.
    Exceeds
}
