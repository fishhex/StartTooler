using System;

namespace StartTooler.Services;

public class ToastService
{
    private static ToastService? _instance;
    public static ToastService Instance => _instance ??= new ToastService();

    public event EventHandler<ToastEventArgs>? ShowRequested;

    public void Show(string message, ToastType type = ToastType.Info)
    {
        ShowRequested?.Invoke(this, new ToastEventArgs(message, type));
    }

    public void Success(string message) => Show(message, ToastType.Success);
    public void Error(string message) => Show(message, ToastType.Error);
    public void Info(string message) => Show(message, ToastType.Info);
}

public class ToastEventArgs : EventArgs
{
    public string Message { get; }
    public ToastType Type { get; }

    public ToastEventArgs(string message, ToastType type)
    {
        Message = message;
        Type = type;
    }
}

public enum ToastType
{
    Info,
    Success,
    Error
}