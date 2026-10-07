using Microsoft.UI.Xaml;

namespace PureProject.WinUI;

public partial class App : Application
{
    private Window? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                var directory = Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR") ?? PureProject.Infrastructure.JsonProjectRepository.DefaultDataDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "crash.log"), $"{DateTimeOffset.UtcNow:O}\n{args.Exception}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
