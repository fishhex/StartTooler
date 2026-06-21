using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Models;

namespace StartTooler.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<TimelineEntry> timelineEntries;
    [ObservableProperty] private TimelineEntry? selectedTimelineEntry;
    [ObservableProperty] private ObservableCollection<Photo> photos;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private Photo? selectedPhoto;

    public GalleryViewModel()
    {
        TimelineEntries = new ObservableCollection<TimelineEntry>();
        Photos = new ObservableCollection<Photo>();

        LoadSampleData();
    }

    private void LoadSampleData()
    {
        TimelineEntries.Add(new TimelineEntry(new DateTime(2017, 3, 10), 12));
        TimelineEntries.Add(new TimelineEntry(new DateTime(2018, 5, 12), 8));
        TimelineEntries.Add(new TimelineEntry(new DateTime(2020, 9, 30), 5));

        SelectedTimelineEntry = TimelineEntries[0];
        TimelineEntries[0].IsSelected = true;
        LoadPhotosForDate(TimelineEntries[0].Date);
    }

    [RelayCommand]
    private void Select(TimelineEntry entry)
    {
        if (entry != null && entry != SelectedTimelineEntry)
        {
            if (SelectedTimelineEntry != null)
                SelectedTimelineEntry.IsSelected = false;
            entry.IsSelected = true;
            SelectedTimelineEntry = entry;
        }
    }

    partial void OnSelectedTimelineEntryChanged(TimelineEntry? value)
    {
        if (value != null)
        {
            LoadPhotosForDate(value.Date);
        }
    }

    private void LoadPhotosForDate(DateTime date)
    {
        IsLoading = true;
        IsEmpty = false;
        HasError = false;
        Photos.Clear();

        Task.Delay(300).ContinueWith(_ =>
        {
            var random = new Random(date.GetHashCode());
            var count = random.Next(0, 15);

            Dispatcher.UIThread.Post(() =>
            {
                if (count == 0)
                {
                    IsEmpty = true;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        var status = (SyncStatus)random.Next(3);
                        Photos.Add(new Photo(
                            $"photo-{date:yyyy-MM-dd}-{i}",
                            date.AddHours(random.Next(0, 12)),
                            null,
                            status,
                            random.Next(1, 15)
                        ));
                    }
                }

                IsLoading = false;
            });
        });
    }

    [RelayCommand]
    private void OpenPhoto(Photo? photo)
    {
        if (photo == null) return;

        try
        {
            Debug.WriteLine($"Open photo: {photo.Id}");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"打开失败: {ex.Message}";
            HasError = true;
        }
    }

    [RelayCommand]
    private void DeletePhoto(Photo? photo)
    {
        if (photo == null) return;

        Photos.Remove(photo);
        if (Photos.Count == 0)
        {
            IsEmpty = true;
        }
    }

    [RelayCommand]
    private void ReuploadPhoto(Photo? photo)
    {
        if (photo == null) return;

        Debug.WriteLine($"Reupload photo: {photo.Id}");
    }

    [RelayCommand]
    private void SelectPhoto(Photo? photo)
    {
        if (photo != null && photo != SelectedPhoto)
        {
            SelectedPhoto = photo;
        }
        else if (photo == SelectedPhoto)
        {
            SelectedPhoto = null;
        }
    }

    [RelayCommand]
    private void ImportPhotos()
    {
        Debug.WriteLine("Import photos");
    }

    [RelayCommand]
    private void Retry()
    {
        if (SelectedTimelineEntry != null)
        {
            LoadPhotosForDate(SelectedTimelineEntry.Date);
        }
    }
}
