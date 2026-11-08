using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using OrgExplorer.Workflow;
using Microsoft.Win32;

namespace OrgExplorer.Desktop;

public partial class PersonPhotoControl : UserControl
{
    public PersonPhotoControl() => InitializeComponent();
    public event Action<string?>? PhotoEdited;
    public bool CanEdit { set => Actions.IsEnabled = value; }

    private void ChoosePhoto(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Local JPEG or PNG|*.jpg;*.jpeg;*.png", Title = "Choose a local Person photo" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) PhotoEdited?.Invoke(dialog.FileName);
    }

    private void ClearPhoto(object sender, RoutedEventArgs e) => PhotoEdited?.Invoke(null);

    public void Show(PersonPhoto? photo)
    {
        PhotoImage.Source = null;
        PhotoImage.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        if (photo is null) return;
        using var stream = new MemoryStream(photo.Bytes.ToArray(), false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        PhotoImage.Source = image;
        AutomationProperties.SetName(PhotoImage, $"Local {photo.ContentType} photo: {image.PixelWidth} × {image.PixelHeight} pixels");
        Placeholder.Visibility = Visibility.Collapsed;
        PhotoImage.Visibility = Visibility.Visible;
    }
}
