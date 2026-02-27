namespace ABC.DiscoveryCity.Services;

public class NotificationService
{
    public event Action<NotificationMessage>? OnNotification;

    public void ShowError(string message) =>
        OnNotification?.Invoke(new NotificationMessage(message, NotificationLevel.Error));

    public void ShowWarning(string message) =>
        OnNotification?.Invoke(new NotificationMessage(message, NotificationLevel.Warning));

    public void ShowSuccess(string message) =>
        OnNotification?.Invoke(new NotificationMessage(message, NotificationLevel.Success));

    public void ShowInfo(string message) =>
        OnNotification?.Invoke(new NotificationMessage(message, NotificationLevel.Info));
}

public record NotificationMessage(string Text, NotificationLevel Level);

public enum NotificationLevel { Info, Success, Warning, Error }
