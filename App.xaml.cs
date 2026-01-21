using System.Configuration;
using System.Data;
using System.Windows;
using Velopack;

namespace EnshroudedPlanner
{
    public partial class App : Application
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // so früh wie möglich:
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}