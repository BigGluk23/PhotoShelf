using System.Collections.ObjectModel;
using System.IO;

namespace PhotoShelf.Desktop;

public sealed class SimilarPhotoGroupViewModel
{
    public SimilarPhotoGroupViewModel(long id, long totalFiles, string referencePath,
        int maximumDifferenceDistance, int maximumAverageDistance, long memberOffset,
        IEnumerable<SimilarPhotoItemViewModel> items)
    {
        Id = id;
        TotalFiles = totalFiles;
        ReferencePath = referencePath;
        MaximumDifferenceDistance = maximumDifferenceDistance;
        MaximumAverageDistance = maximumAverageDistance;
        MemberOffset = memberOffset;
        Items = new ObservableCollection<SimilarPhotoItemViewModel>(items);
    }

    public long Id { get; }
    public long TotalFiles { get; }
    public string ReferencePath { get; }
    public int MaximumDifferenceDistance { get; }
    public int MaximumAverageDistance { get; }
    public long MemberOffset { get; }
    public ObservableCollection<SimilarPhotoItemViewModel> Items { get; }
    public string Summary => $"Серия {Id}: {TotalFiles:N0} фото";
    public string ReferenceFolder => Path.GetDirectoryName(ReferencePath) ?? ReferencePath;
    public string DistanceSummary => $"Разброс: {MaximumDifferenceDistance} / {MaximumAverageDistance}";
}

public sealed class SimilarPhotoItemViewModel
{
    public SimilarPhotoItemViewModel(PhotoItem photo, int differenceDistance,
        int averageDistance, bool isReference)
    {
        Photo = photo;
        DifferenceDistance = differenceDistance;
        AverageDistance = averageDistance;
        IsReference = isReference;
    }

    public PhotoItem Photo { get; }
    public int DifferenceDistance { get; }
    public int AverageDistance { get; }
    public bool IsReference { get; }
    public int SimilarityPercent => IsReference ? 100 : Math.Clamp(
        (int)Math.Round(100 - (DifferenceDistance * 0.7 + AverageDistance * 0.3) / 64d * 100), 0, 100);
    public string SimilarityText => IsReference ? "Эталон группы" : $"Визуальная близость: {SimilarityPercent}%";
}
