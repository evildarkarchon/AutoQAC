using System.Windows;

namespace PACT.UI;

public partial class App : Application
{
    public App()
    {
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}