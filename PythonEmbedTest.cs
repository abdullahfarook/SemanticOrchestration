using CSnakes.Runtime;
using CSnakes.Runtime.Locators;
using CSnakes.Runtime.PackageManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace SemanticOrchestration;

public class PythonEmbedTest
{
    public static void Test()
    {
        var pythonHomePath = AppContext.BaseDirectory;

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddFilter("CSnakes", LogLevel.Debug);
        builder.Services
            .WithPython()
            .WithHome(pythonHomePath)
            .FromRedistributable(RedistributablePythonVersion.Python3_10)
            .WithVirtualEnvironment(Path.Combine(pythonHomePath, ".venv"))
            .WithPipInstaller();

        var app = builder.Build();
        var installer = app.Services.GetRequiredService<IPythonPackageInstaller>();
// await installer.InstallPackagesFromRequirements("requirements.txt");
// await installer.InstallPackage("llmsherpa");
        var env = app.Services.GetRequiredService<IPythonEnvironment>();

        var hello = env.Hello();
        Console.WriteLine(hello.Greetings("World"));
        Console.WriteLine(hello.ReadPdf("World"));
    }
}