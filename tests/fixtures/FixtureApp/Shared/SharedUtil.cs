namespace Shared;

// Linked (Compile Include) into BOTH FixtureCore and FixtureConsumer, so the same declaration is
// compiled into two assemblies. Exercises identity keying: a shared file must NOT collapse to one
// symbol the way a multi-TFM duplicate does — it is genuinely two distinct declarations.
public static class SharedUtil
{
    public static int Combine(int a, int b) => a + b;
}
