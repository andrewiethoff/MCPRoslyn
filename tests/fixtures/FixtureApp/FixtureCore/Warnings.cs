namespace FixtureCore;

// Deliberately carries one CS0219 (assigned-but-unused local 'x') so speculative-edit tests can
// add a second, identically-worded CS0219 and verify multiplicity is not lost by the diff.
public static class Warnings
{
    public static void A()
    {
        int x = 1;
    }
}
