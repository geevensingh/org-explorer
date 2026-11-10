namespace OrgExplorer.Desktop.Smoke;

internal static class NavigationRejection
{
    internal static void AssertPending(Action invoke, Func<string> readStatus, Func<string> readEmail,
        string expectedEmail, Action<Func<bool>> awaitFeedback)
    {
        invoke();
        var status = "";
        try
        {
            awaitFeedback(() =>
            {
                status = readStatus();
                var email = readEmail();
                if (email != expectedEmail)
                    throw new InvalidOperationException($"Pending autosave navigation changed the selected Person from {expectedEmail} to {email}.");
                return status.StartsWith("Navigation blocked:", StringComparison.Ordinal) &&
                    status.Contains("Please wait", StringComparison.Ordinal);
            });
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException("Pending autosave navigation was not visibly rejected. Last status: " + status, error);
        }
    }
}
