namespace DbDelta.Core.Comparison;

public enum NarrowingKind
{
    // The change cannot lose anything: same type, or a strictly wider one.
    None = 0,

    // Length, precision or scale goes down. The server may round silently rather than refuse, which is
    // what makes this worth naming separately from a conversion that fails.
    Narrows = 1,

    // A different type family. Whether it survives depends on the values, and the server decides at
    // apply time; either way it is not something to run without saying so first.
    Converts = 2
}
