using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

public partial class MediaFileDateGroup : ObservableObject
{
    [ObservableProperty]
    private string _dateHeader = string.Empty;

    [ObservableProperty]
    private DateTime _date;

    [ObservableProperty]
    private ObservableCollection<MediaFile> _files = new();

    [ObservableProperty]
    private ObservableCollection<MediaBurstGroup> _burstGroups = new();
}
