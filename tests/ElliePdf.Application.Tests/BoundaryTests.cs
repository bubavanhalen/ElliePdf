using System.Xml.Linq;
using Xunit;

namespace ElliePdf.Application.Tests;

public sealed class BoundaryTests
{
    [Fact]
    public void Application_sources_contain_no_ui_framework_types()
    {
        string root = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(root, "src", "ElliePdf.Application");
        string[] forbidden = ["BitmapImage", "ContentDialog", "InfoBarSeverity", "XamlRoot", "Microsoft.UI", "WinUI"];

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain(forbidden, value => source.Contains(value, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Application_references_only_domain_and_transport_contracts()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root, "src", "ElliePdf.Application", "ElliePdf.Application.csproj"));
        string[] references = project.Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension((string)element.Attribute("Include")!))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ElliePdf.Domain", "ElliePdf.Pdf.Contracts"], references);
    }

    [Fact]
    public void Domain_contracts_and_rendering_sources_are_transport_neutral()
    {
        string root = FindRepositoryRoot();
        string[] sourceDirectories =
        [
            Path.Combine(root, "src", "ElliePdf.Domain"),
            Path.Combine(root, "src", "ElliePdf.Pdf.Contracts"),
            Path.Combine(root, "src", "ElliePdf.Rendering")
        ];
        string[] forbidden =
        [
            "Microsoft.UI",
            "Microsoft.Windows",
            "Windows.",
            "WinUI",
            "BitmapImage",
            "ContentDialog",
            "InfoBarSeverity",
            "XamlRoot",
            "PdfiumNative",
            "pdfium.dll"
        ];

        foreach (string directory in sourceDirectories)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(file);
                Assert.DoesNotContain(forbidden, value => source.Contains(value, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Pdfium_project_references_are_confined_to_worker_and_integration_tests()
    {
        string root = FindRepositoryRoot();
        string[] allowed =
        [
            Path.Combine("src", "ElliePdf.Pdfium.Worker", "ElliePdf.Pdfium.Worker.csproj"),
            Path.Combine("tests", "ElliePdf.Pdfium.IntegrationTests", "ElliePdf.Pdfium.IntegrationTests.csproj")
        ];

        foreach (string projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            string[] references = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(element => Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(projectPath)!,
                    (string)element.Attribute("Include")!)))
                .ToArray();
            if (!references.Any(reference =>
                string.Equals(Path.GetFileNameWithoutExtension(reference), "ElliePdf.Pdfium", StringComparison.Ordinal)))
            {
                continue;
            }

            Assert.Contains(
                allowed,
                allowedPath => string.Equals(
                    Path.GetFullPath(Path.Combine(root, allowedPath)),
                    Path.GetFullPath(projectPath),
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Pdfium_native_pinvoke_is_declared_only_in_the_pdfium_adapter()
    {
        string root = FindRepositoryRoot();
        string adapterDirectory = Path.Combine(root, "src", "ElliePdf.Pdfium");

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.StartsWith(adapterDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source = File.ReadAllText(file);
            Assert.DoesNotContain(
                "LibraryImport(\"pdfium.dll",
                source,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "DllImport(\"pdfium.dll",
                source,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Application_and_domain_have_no_static_workspace_or_tab_state()
    {
        string root = FindRepositoryRoot();
        string[] sourceDirectories =
        [
            Path.Combine(root, "src", "ElliePdf.Application"),
            Path.Combine(root, "src", "ElliePdf.Domain")
        ];

        foreach (string file in sourceDirectories.SelectMany(static directory =>
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)))
        {
            string source = File.ReadAllText(file);
            foreach (string line in source.Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                if (line.Contains("static", StringComparison.Ordinal)
                    && (line.Contains("DocumentWorkspace", StringComparison.Ordinal)
                        || line.Contains("DocumentContext", StringComparison.Ordinal)
                        || line.Contains("DocumentTab", StringComparison.Ordinal)))
                {
                    Assert.Fail($"Static workspace/tab state is not allowed: {file}: {line.Trim()}");
                }
            }
        }
    }

    [Fact]
    public void Winui_consumers_use_injected_dependencies_instead_of_static_app_state()
    {
        string root = FindRepositoryRoot();
        string appSource = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
        string[] staticHostMembers =
        [
            "public static Window Window",
            "public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue",
            "public static nint WindowHandle",
            "public static IServiceProvider Services"
        ];
        Assert.DoesNotContain(
            staticHostMembers,
            member => appSource.Contains(member, StringComparison.Ordinal));

        string[] uiDirectories =
        [
            root,
            Path.Combine(root, "Controls"),
            Path.Combine(root, "Dialogs"),
            Path.Combine(root, "Helpers"),
            Path.Combine(root, "Models"),
            Path.Combine(root, "Navigation"),
            Path.Combine(root, "Pages"),
            Path.Combine(root, "Services"),
            Path.Combine(root, "ViewModels")
        ];
        string[] forbidden =
        [
            "App.Services",
            "App.Window",
            "App.WindowHandle",
            "App.DispatcherQueue"
        ];

        IEnumerable<string> uiSources = uiDirectories.SelectMany(directory =>
            Directory.EnumerateFiles(
                directory,
                "*.cs",
                directory == root ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories));
        foreach (string file in uiSources)
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain(forbidden, value => source.Contains(value, StringComparison.Ordinal));
            if (!string.Equals(file, Path.Combine(root, "App.xaml.cs"), StringComparison.OrdinalIgnoreCase))
            {
                Assert.DoesNotContain("GetRequiredService", source, StringComparison.Ordinal);
            }
        }

        Assert.Contains("AddSingleton<UiHostContext>()", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_and_client_background_loops_have_explicit_lifetimes()
    {
        string root = FindRepositoryRoot();
        string workerServer = File.ReadAllText(Path.Combine(
            root, "src", "ElliePdf.Pdfium.Worker", "PdfWorkerServer.cs"));
        string workerClient = File.ReadAllText(Path.Combine(
            root, "src", "ElliePdf.Pdf.Client", "PdfWorkerClient.cs"));

        Assert.DoesNotContain("_ = WriteErrorAsync", workerServer, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueWith", workerServer, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = Task.Run", workerClient, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
