using System.Collections.ObjectModel;
using PhotoShelf.Application.Catalog;
using PhotoShelf.Infrastructure.Sqlite;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoShelf.Desktop;

public partial class PhotoViewerWindow : Window
{
    private readonly SqliteDesktopCatalogStore _store;
    private readonly CatalogViewQuery _query;
    private readonly long _count;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private readonly Dictionary<long, PhotoItem> _nearby = new();
    private readonly DispatcherTimer _videoTimer;
    private long _index;
    private string? _anchorPath;
    private string? _displayedPath;
    private bool _updatingFilmstrip;
    private bool _isSeeking;
    private bool _isVideoPlaying;

    public PhotoViewerWindow(SqliteDesktopCatalogStore store, CatalogViewQuery query, long count, long index, string? anchorPath = null)
    {
        InitializeComponent();
        _store = store; _query = query; _count = count; _anchorPath = anchorPath;
        DataContext = this;
        _index = Math.Clamp(index, 0, Math.Max(0, count - 1));
        _videoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _videoTimer.Tick += OnVideoTimerTick;
        ShowCurrentPhoto();
    }


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
        if (_count == 0)
        {
            return;
        }

        _index = (_index + delta + _count) % _count;
        ShowCurrentPhoto();
    }

    private async void ShowCurrentPhoto()
    {
        if (_count == 0)
        {
            return;
        }

        _request?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        StopVideo(); AsyncMediaImage.SetPath(PhotoImage, null); TitleText.Text = "Загружаю…";
        PhotoItem item;
        try
        {
            var anchor = _anchorPath; _anchorPath = null;
            var center = _index;
            if (anchor is not null)
            {
                var movedIndex = await _store.IndexOfAsync(_query, anchor, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (movedIndex is not null) _index = center = movedIndex.Value;
            }
            var start = Math.Max(0, center - 5);
            var page = await _store.QueryPageAsync(_query, checked((int)start), 11, token: request.Token);
            request.Token.ThrowIfCancellationRequested();
            _nearby.Clear();
            for (var i = 0; i < page.Items.Count; i++)
            {
                var saved = page.Items[i];
                var photo = new PhotoItem(saved.Path, saved.SizeBytes, saved.FileModifiedAt) { IsFavorite = saved.IsFavorite };
                photo.ApplyCatalogObservation(saved);
                _nearby[start + i] = photo;
            }
            if (!_nearby.TryGetValue(center, out item!)) { TitleText.Text = "Файл больше не входит в этот вид"; return; }
            if (anchor is not null && !item.Path.Equals(anchor, StringComparison.OrdinalIgnoreCase))
            {
                var saved = await _store.GetItemAsync(anchor, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (saved is null) { TitleText.Text = "Выбранный файл больше не в каталоге"; return; }
                item = new PhotoItem(saved.Path, saved.SizeBytes, saved.FileModifiedAt);
                item.ApplyCatalogObservation(saved);
                _nearby[center] = item;
            }
            RebuildFilmstrip();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { TitleText.Text = $"Не удалось загрузить: {ex.Message}"; return; }
        finally { if (ReferenceEquals(_request, request)) _request = null; }

        Title = item.FileName;
        _displayedPath = item.Path;
        TitleText.Text = item.FileName;
        DetailText.Text = $"{_index + 1} из {_count}   {item.DetailLine}   {item.Folder}";
        StopVideo();
        AsyncMediaImage.SetPath(PhotoImage, null);

        if (item.PreviewPath is null)
        {
            VideoControlsPanel.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Collapsed;
            PhotoImage.Visibility = Visibility.Visible;
            AsyncMediaImage.SetStatus(PhotoImage, item.AvailabilityText);
            return;
        }

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
        PhotoImage.Source = null;

        AsyncMediaImage.SetDecodeWidth(PhotoImage, 2200);
        AsyncMediaImage.SetRevision(PhotoImage, item.ObservationVersion);
        AsyncMediaImage.SetPath(PhotoImage, item.Path);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel(); _request?.Cancel();
        StopVideo();
        AsyncMediaImage.SetPath(PhotoImage, null);
        base.OnClosed(e);
    }

    public void CatalogItemsChanged(IReadOnlyList<SavedMediaItem> items, IReadOnlyList<CatalogExternalRename> renames)
    {
        if (_lifetime.IsCancellationRequested) return;
        var path = _displayedPath;
        if (path is null) return;
        var rename = renames.FirstOrDefault(change => change.Source.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (rename is null && !items.Any(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        _anchorPath = rename?.Destination.Path ?? path;
        ShowCurrentPhoto();
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
        _updatingFilmstrip = true; FilmstripItems.Clear();
        foreach (var (index, item) in _nearby.OrderBy(x => x.Key))
        {
            var center = index == _index;
            FilmstripItems.Add(new FilmstripCell(item, index, center, center ? 122 : 84, center ? 88 : 64));
        }
        FilmstripList.SelectedItem = FilmstripItems.FirstOrDefault(x => x.IsCenter);
        _updatingFilmstrip = false;
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public sealed record FilmstripCell(PhotoItem Item, long AbsoluteIndex, bool IsCenter, double Width, double Height);
}
