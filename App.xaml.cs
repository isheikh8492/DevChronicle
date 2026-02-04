using System.Windows;
using DevChronicle.Services;
using DevChronicle.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DevChronicle
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public static IServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            ServiceProvider = services.BuildServiceProvider();

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Register core services
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<GitService>();
            services.AddSingleton<ClusteringService>();
            services.AddSingleton<MiningService>();
            services.AddSingleton<SummarizationService>();
            services.AddSingleton<ExportService>();

            // Register ViewModels
            services.AddTransient<SessionsViewModel>();
            services.AddTransient<MiningViewModel>();
            services.AddTransient<DashboardViewModel>();

            // Register Windows and Pages
            services.AddSingleton<MainWindow>();
        }
    }
}
