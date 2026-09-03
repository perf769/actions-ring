using System.Windows;

namespace ActionsRing.App.Windows;

public partial class ProfileNameWindow : Window
{
    public ProfileNameWindow(string title, string initialName = "")
    {
        InitializeComponent();
        HeadingText.Text = title;
        NameBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string ProfileName { get; private set; } = string.Empty;

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ValidationText.Text = "Введите название.";
            return;
        }

        ProfileName = name;
        DialogResult = true;
    }
}
