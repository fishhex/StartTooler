using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

public partial class MediaBurstGroup : ObservableObject
{
    public MediaBurstGroup(IEnumerable<MediaFile> files)
    {
        Files = new ObservableCollection<MediaFile>(files);
        Files.CollectionChanged += OnFilesChanged;
    }

    public ObservableCollection<MediaFile> Files { get; }

    public MediaFile? Cover => Files.FirstOrDefault();

    public string BadgeText => $"📂 {Files.Count} 张";

    public bool HasMultiple => Files.Count > 1;

    private void OnFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(HasMultiple));
        OnPropertyChanged(nameof(Cover));
    }
}
