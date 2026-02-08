using System.Windows;
using DevChronicle.Services;
using DevChronicle.ViewModels;
using DevChronicle.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;
        private static LoggerService? _logger;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Initialize logger FIRST before anything else
            _logger = new LoggerService();
            _logger.LogInfo("Application starting...");

            // Setup global exception handlers
            SetupExceptionHandlers();

            try
            {
                _logger.LogInfo("Configuring dependency injection...");
                var services = new ServiceCollection();
                ConfigureServices(services);
                ServiceProvider = services.BuildServiceProvider();
                _logger.LogInfo("Dependency injection configured successfully");

                _logger.LogInfo("Creating main window...");
                var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
                _logger.LogInfo("Main window created successfully");

                _logger.LogInfo("Showing main window...");
                mainWindow.Show();
                _logger.LogInfo("Application started successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogCritical("FATAL ERROR during application startup", ex);
                _logger?.LogInfo($"Check log file at: {_logger?.GetLogFilePath()}");
                Shutdown(1);
            }
        }

        private void SetupExceptionHandlers()
        {
            // Catch all unhandled exceptions in any thread
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                var exception = args.ExceptionObject as Exception;
                _logger?.LogCritical("UNHANDLED EXCEPTION (AppDomain)", exception);
                // App will crash after this - check logs/ folder for details
            };

            // Catch all unhandled exceptions on the UI thread
            DispatcherUnhandledException += (sender, args) =>
            {
                _logger?.LogCritical("UNHANDLED EXCEPTION (UI Thread)", args.Exception);
                args.Handled = true; // Prevent app from crashing immediately
            };

            _logger?.LogInfo("Global exception handlers configured");
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Register logger as singleton (use the already-created instance)
            if (_logger != null)
            {
                services.AddSingleton(_logger);
                _logger.LogInfo("Logger registered in DI container");
            }

            // Register core services
            _logger?.LogInfo("Registering core services...");
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<GitService>();
            services.AddSingleton<ClusteringService>();
            services.AddSingleton<MiningService>();
            services.AddSingleton<SummarizationService>();
            services.AddSingleton<SummarizationRunnerService>();
            services.AddSingleton<ExportService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<SessionContextService>();

            // Register ViewModels
            _logger?.LogInfo("Registering ViewModels...");
            services.AddTransient<SessionsViewModel>();
            services.AddTransient<MiningViewModel>();
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<SessionDetailViewModel>();
            services.AddTransient<SummarizationViewModel>();
            services.AddTransient<ExportViewModel>();
            services.AddTransient<DayBrowserViewModel>();
            services.AddTransient<SettingsViewModel>();

            // Register Windows and Pages
            _logger?.LogInfo("Registering Windows and Pages...");
            services.AddSingleton<MainWindow>();
        }
    }
}
