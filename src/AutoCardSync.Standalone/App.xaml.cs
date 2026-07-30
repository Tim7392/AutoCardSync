using System.Diagnostics;
using System.IO;
using AutoCardSync.Standalone.Core.Configuration;
using AutoCardSync.Standalone.Core;
using AutoCardSync.Standalone.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoCardSync.Standalone;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private bool _restartRequested;

    public void RequestRestart()
    {
        _restartRequested = true;
        Shutdown();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (await TryRunMaintenanceCommandAsync(e.Args))
            return;

        _singleInstance = SingleInstanceCoordinator.Acquire();
        if (!_singleInstance.IsPrimary)
        {
            _singleInstance.SignalPrimaryInstance();
            Shutdown();
            return;
        }

        try
        {
            _host = Host.CreateDefaultBuilder(e.Args)
                .ConfigureServices(services =>
                {
                    services.AddLogging(builder => builder.AddDebug());
                    services.AddSingleton<StandaloneDataPaths>();
                    services.AddSingleton(provider =>
                    {
                        var paths = provider.GetRequiredService<StandaloneDataPaths>();
                        return new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
                    });
                    services.AddSingleton<LoginAutoStartService>();
                    services.AddSingleton<StandaloneConfigurationService>();
                    services.AddSingleton<StandaloneRuntimeService>();
                    services.AddHostedService(provider => provider.GetRequiredService<StandaloneRuntimeService>());
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            MainWindow window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            _singleInstance.ListenForActivation(() =>
                Dispatcher.BeginInvoke(window.ActivateFromSecondaryLaunch));
            window.Show();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"AutoCardSync 无法启动。\n\n{exception.Message}",
                "AutoCardSync",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private async Task<bool> TryRunMaintenanceCommandAsync(string[] args)
    {
        bool reconcile = args.Any(argument =>
            string.Equals(argument, "--reconcile-autostart", StringComparison.OrdinalIgnoreCase));
        bool remove = args.Any(argument =>
            string.Equals(argument, "--remove-autostart", StringComparison.OrdinalIgnoreCase));
        if (!reconcile && !remove)
            return false;

        try
        {
            var autoStart = new LoginAutoStartService();
            bool desired = false;
            if (reconcile && !remove)
            {
                var paths = new StandaloneDataPaths();
                var store = new AtomicJsonFileStore<StandaloneConfiguration>(paths.ConfigurationFile);
                try
                {
                    StandaloneConfiguration? configuration = await store.LoadAsync(CancellationToken.None);
                    desired = configuration is not null && configuration.Validate().IsValid &&
                        configuration.NormalizeForCurrentSchema().AutoStartOnLogin;
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException)
                {
                    _ = exception;
                    desired = false;
                }
            }
            autoStart.SetEnabled(desired);
            Shutdown(0);
        }
        catch
        {
            Shutdown(-1);
        }
        return true;
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }
        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
        if (_restartRequested && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath)
            {
                UseShellExecute = true,
            });
        }
    }
}
