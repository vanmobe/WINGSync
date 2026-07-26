namespace WingSync.Core.Tests;

internal static class Program
{
    public static int Main()
    {
        var suite = new TestSuite();
        DomainTests.Register(suite);
        ConfigValidatorTests.Register(suite);
        TokenAndScopeTests.Register(suite);
        PlanningTests.Register(suite);
        return suite.Run();
    }
}
