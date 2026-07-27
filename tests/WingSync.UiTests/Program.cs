using System.Text;
using System.IO;

namespace WingSync.UiTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var options = UiTestEnvironment.ParseOptions(arguments);
            Console.WriteLine($"WingSync UI application: {Path.GetFullPath(options.ApplicationPath)}");
            Console.WriteLine($"UI artifacts: {Path.GetFullPath(options.ArtifactsDirectory)}");
            var environment = new UiTestEnvironment(
                options.ApplicationPath,
                options.ArtifactsDirectory);
            if (options.GuideScreenshotsDirectory is not null)
            {
                GuideScreenshotScenarios.Capture(
                    environment,
                    options.GuideScreenshotsDirectory);
                return 0;
            }

            var suite = new TestSuite();
            UiScenarios.Register(suite, environment);
            return suite.Run(options.Filter);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (FileNotFoundException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }
}
