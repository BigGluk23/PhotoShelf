using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfAnimatedGif;

namespace PhotoShelf.Desktop;

public partial class PhotoViewerWindow : Window, INotifyPropertyChanged
{
    private readonly IReadOnlyList<PhotoItem> _items;
    private readonly DispatcherTimer _videoTimer;
    private int _index;
    private bool _updatingFilmstrip;
    private bool _isSeeking;
    private bool _isVideoPlaying;

    public PhotoViewerWindow(IReadOnlyList<PhotoItem> items, int index)
    {
        InitializeComponent();
        _items = items;
        DataContext = this;
        _index = Math.Clamp(index, 0, Math.Max(0, items.Count - 1));
        _videoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _videoTimer.Tick += OnVideoTimerTick;
        ShowCurrentPhoto();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FilmstripCell> FilmstripItems { get; } = new();

    private void OnPreviousClicked(object sender, RoutedEventArgs e) => Move(-1);

    private void OnNextClicked(object sender, RoutedEventArgs e) => Move(1);

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            Move(-1);
        }
        else if (e.Key == Key.Right)
        {
            Move(1);
        }
        else if (e.Key == Key.Space && VideoPlayer.Visibility == Visibility.Visible)
        {
            ToggleVideoPlayback();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnFilmstripSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingFilmstrip || FilmstripList.SelectedItem is not FilmstripCell cell || cell.IsCenter)
        {
            return;
        }

        _index = cell.AbsoluteIndex;
        ShowCurrentPhoto();
    }

    private void Move(int delta)
    {
        if (_items.Count == 0)
        {
            return;
        }

        _index = (_index + delta + _items.Count) % _items.Count;
        ShowCurrentPhoto();
    }

    private void ShowCurrentPhoto()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var item = _items[_index];
        RebuildFilmstrip();

        Title = item.FileName;
        TitleText.Text = item.FileName;
        DetailText.Text = $"{_index + 1} из {_items.Count}   {item.DetailLine}   {item.Folder}";
        StopVideo();

        if (item.IsVideo)
        {
            PhotoImage.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;
            VideoControlsPanel.Visibility = Visibility.Visible;
            VideoSeekSlider.Value = 0;
            VideoPositionText.Text = "0:00";
            VideoDurationText.Text = "0:00";
            PlayPauseButton.Content = "Пауза";
            VideoPlayer.Source = new Uri(item.Path, UriKind.Absolute);
            VideoPlayer.Play();
            _isVideoPlaying = true;
            _videoTimer.Start();
            return;
        }

        VideoControlsPanel.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Collapsed;
        PhotoImage.Visibility = Visibility.Visible;
        ImageBehavior.SetAnimatedSource(PhotoImage, null);
        PhotoImage.Source = null;

        if (PhotoItem.IsWebpPath(item.Path))
        {
            PhotoImage.Source = ImageSharpBitmapLoader.TryLoad(item.Path, 2200);
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(item.Path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            if (PhotoItem.IsAnimatedGifPath(item.Path))
            {
                ImageBehavior.SetAnimatedSource(PhotoImage, bitmap);
            }
            else
            {
                PhotoImage.Source = bitmap;
            }
        }
        catch
        {
            PhotoImage.Source = null;
        }
    }

    private void StopVideo()
    {
        _videoTimer.Stop();
        _isSeeking = false;
        _isVideoPlaying = false;
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
    }

    private void OnViewerSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var x = e.GetPosition((IInputElement)sender).X;
        Move(x >= ActualWidth / 2 ? 1 : -1);
    }

    private void OnVideoOpened(object sender, RoutedEventArgs e)
    {
        if (!VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        VideoSeekSlider.Maximum = Math.Max(1, VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds);
        VideoDurationText.Text = FormatTime(VideoPlayer.NaturalDuration.TimeSpan);
    }

    private void OnVideoEnded(object sender, RoutedEventArgs e)
    {
        _isVideoPlaying = false;
        PlayPauseButton.Content = "Пуск";
        _videoTimer.Stop();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        ToggleVideoPlayback();
    }

    private void ToggleVideoPlayback()
    {
        if (_isVideoPlaying)
        {
            VideoPlayer.Pause();
            _isVideoPlaying = false;
            PlayPauseButton.Content = "Пуск";
            return;
        }

        VideoPlayer.Play();
        _isVideoPlaying = true;
        PlayPauseButton.Content = "Пауза";
        _videoTimer.Start();
    }

    private void OnVideoSeekStarted(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
    }

    private void OnVideoSeekFinished(object sender, MouseButtonEventArgs e)
    {
        SeekVideoToSlider();
        _isSeeking = false;
    }

    private void OnVideoSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking)
        {
            VideoPositionText.Text = FormatTime(TimeSpan.FromSeconds(VideoSeekSlider.Value));
        }
    }

    private void OnVideoTimerTick(object? sender, EventArgs e)
    {
        if (_isSeeking || VideoPlayer.Visibility != Visibility.Visible)
        {
            return;
        }

        VideoSeekSlider.Value = Math.Min(VideoSeekSlider.Maximum, VideoPlayer.Position.TotalSeconds);
        VideoPositionText.Text = FormatTime(VideoPlayer.Position);
    }

    private void SeekVideoToSlider()
    {
        VideoPlayer.Position = TimeSpan.FromSeconds(VideoSeekSlider.Value);
        VideoPositionText.Text = FormatTime(VideoPlayer.Position);
    }

    private void RebuildFilmstrip()
    {
        _updatingFilmstrip = true;
        FilmstripItems.Clear();
        const int radius = 5;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var absoluteIndex = (_index + offset + _items.Count) % _items.Count;
            var distance = Math.Abs(offset);
            FilmstripItems.Add(new FilmstripCell(
                _items[absoluteIndex],
                absoluteIndex,
                offset == 0,
                distance switch
                {
                    0 => 122,
                    1 => 96,
                    2 => 84,
                    _ => 72
                },
                distance switch
                {
                    0 => 88,
                    1 => 72,
                    2 => 64,
                    _ => 56
                }));
        }

        FilmstripList.SelectedIndex = radius;
        _updatingFilmstrip = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilmstripItems)));
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public sealed record FilmstripCell(PhotoItem Item, int AbsoluteIndex, bool IsCenter, double Width, double Height);
}
