using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;

namespace PhotoShelf.Desktop;

public partial class PhotoCompareWindow : Window
{
    private readonly PhotoItem[] _photos;
    private readonly List<(ScaleTransform Scale, TranslateTransform Pan)> _transforms = [];
    private Point _dragOrigin;
    private double _dragStartX;
    private double _dragStartY;
    private double _panX;
    private double _panY;
    private Border? _capturedViewport;

    public PhotoCompareWindow(IEnumerable<PhotoItem> photos)
    {
        _photos = photos.Take(5).ToArray();
        if (_photos.Length is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(photos), "Для сравнения нужны от двух до четырёх файлов.");
        InitializeComponent();
        BuildComparisonGrid();
    }

    private void BuildComparisonGrid()
    {
        ComparisonGrid.Children.Clear(); ComparisonGrid.ColumnDefinitions.Clear(); ComparisonGrid.RowDefinitions.Clear();
        ComparisonGrid.ColumnDefinitions.Add(new ColumnDefinition());
        ComparisonGrid.ColumnDefinitions.Add(new ColumnDefinition());
        var rowCount = (_photos.Length + 1) / 2;
        for (var row = 0; row < rowCount; row++) ComparisonGrid.RowDefinitions.Add(new RowDefinition());

        for (var index = 0; index < _photos.Length; index++)
        {
            var tile = CreateTile(_photos[index]);
            Grid.SetColumn(tile, index % 2); Grid.SetRow(tile, index / 2);
            ComparisonGrid.Children.Add(tile);
        }
    }

    private Border CreateTile(PhotoItem photo)
    {
        var scale = new ScaleTransform(1, 1);
        var pan = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(pan);
        _transforms.Add((scale, pan));

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            RenderTransform = transforms,
            RenderTransformOrigin = new Point(0.5, 0.5),
            SnapsToDevicePixels = true
        };
        AsyncMediaImage.SetPath(image, photo.Path);

        var viewport = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(12, 13, 16)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(61, 65, 73)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = image,
            Cursor = Cursors.Hand
        };
        viewport.PreviewMouseWheel += OnViewportMouseWheel;
        viewport.MouseLeftButtonDown += OnViewportMouseLeftButtonDown;
        viewport.MouseMove += OnViewportMouseMove;
        viewport.MouseLeftButtonUp += OnViewportMouseLeftButtonUp;
        viewport.LostMouseCapture += OnViewportLostMouseCapture;

        var name = new TextBlock
        {
            Text = photo.FileName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var details = new TextBlock
        {
            Text = $"{photo.DetailLine}  ·  {photo.Folder}",
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(174, 179, 188)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = photo.Path
        };
        var header = new StackPanel { Margin = new Thickness(2, 0, 2, 8) };
        header.Children.Add(name); header.Children.Add(details);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(header); Grid.SetRow(viewport, 1); layout.Children.Add(viewport);

        return new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromRgb(35, 38, 44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 69, 78)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = layout
        };
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ZoomText is null) return;
        ZoomText.Text = $"{e.NewValue * 100:0}%";
        ApplyTransform();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ZoomSlider.Value = Math.Clamp(ZoomSlider.Value * factor, ZoomSlider.Minimum, ZoomSlider.Maximum);
        e.Handled = true;
    }

    private void OnViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border viewport) return;
        _capturedViewport = viewport; _dragOrigin = e.GetPosition(this);
        _dragStartX = _panX; _dragStartY = _panY;
        viewport.Cursor = Cursors.SizeAll; viewport.CaptureMouse(); e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_capturedViewport is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        _panX = _dragStartX + point.X - _dragOrigin.X;
        _panY = _dragStartY + point.Y - _dragOrigin.Y;
        ApplyTransform(); e.Handled = true;
    }

    private void OnViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_capturedViewport is null) return;
        _capturedViewport.ReleaseMouseCapture(); e.Handled = true;
    }

    private void OnViewportLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is Border viewport) viewport.Cursor = Cursors.Hand;
        _capturedViewport = null;
    }

    private void ApplyTransform()
    {
        foreach (var transform in _transforms)
        {
            transform.Scale.ScaleX = ZoomSlider?.Value ?? 1;
            transform.Scale.ScaleY = ZoomSlider?.Value ?? 1;
            transform.Pan.X = _panX; transform.Pan.Y = _panY;
        }
    }

    private void ResetView()
    {
        _panX = 0; _panY = 0; ZoomSlider.Value = 1; ApplyTransform();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e) => ResetView();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.R) { ResetView(); e.Handled = true; }
    }
}
