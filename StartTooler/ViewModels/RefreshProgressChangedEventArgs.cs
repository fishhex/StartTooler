using System;

namespace StartTooler.ViewModels;

public class RefreshProgressChangedEventArgs : EventArgs
{
    public RefreshProgressChangedEventArgs(string message, int current, int total, bool isIndeterminate)
    {
        Message = message;
        Current = current;
        Total = total;
        IsIndeterminate = isIndeterminate;
    }

    public string Message { get; }
    public int Current { get; }
    public int Total { get; }
    public bool IsIndeterminate { get; }
}
