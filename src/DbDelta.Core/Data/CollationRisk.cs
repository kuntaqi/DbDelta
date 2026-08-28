namespace DbDelta.Core.Data;

public enum CollationRisk
{
    // The comparison would still be right; the difference is worth knowing but changes no answer.
    Advisory,

    // The comparison would give a wrong answer, or the write it leads to would not round-trip.
    Blocking
}
