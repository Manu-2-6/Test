using System.Windows;

namespace SoftwareSetupApp;

public partial class PostInstallReminderWindow : Window
{
    public PostInstallReminderWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
