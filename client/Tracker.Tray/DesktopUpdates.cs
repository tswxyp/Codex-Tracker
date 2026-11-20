using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace CodexTracker.Tray;

internal enum UpdateState { Idle, Checking, Downloading, Ready, Current, Failed }

internal sealed class DesktopUpdates : IDisposable
{
    // The public stable releases are sufficient. Users do not need a GitHub account.
    readonly UpdateManager manager = new(new GithubSource(CommunityDialog.Repository, null, prerelease: false));
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15_000 };
    readonly CancellationTokenSource lifetime = new();
    VelopackAsset? pending;
    internal UpdateState State { get; private set; }
    internal bool Installed => manager.IsInstalled && !manager.IsPortable;
    internal event Action? Changed;

    internal void Start()
    {
        if (!Installed) return;
        timer.Tick += OnTimer;
        timer.Start();
    }

    async void OnTimer(object? sender, EventArgs e) => await Check();

    internal async Task Check()
    {
        if (!Installed || State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready) return;
        timer.Stop();
        var token = lifetime.Token;
        SetState(UpdateState.Checking);
        try
        {
            var update = await manager.CheckForUpdatesAsync();
            if (token.IsCancellationRequested) return;
            if (update == null)
            {
                SetState(UpdateState.Current);
                return;
            }
            SetState(UpdateState.Downloading);
            await manager.DownloadUpdatesAsync(update, cancelToken: token);
            if (token.IsCancellationRequested) return;
            pending = update.TargetFullRelease;
            SetState(UpdateState.Ready);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            SetState(UpdateState.Failed);
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        finally
        {
            if (!token.IsCancellationRequested && State != UpdateState.Ready)
            {
                timer.Interval = (int)(State == UpdateState.Failed ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(6)).TotalMilliseconds;
                timer.Start();
            }
        }
    }

    internal bool ScheduleRestart()
    {
        if (pending == null) return false;
        try
        {
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true);
            return true;
        }
        catch (Exception)
        {
            // Keep the downloaded package for another attempt or the next launch.
            return false;
        }
    }

    void SetState(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }

    internal const string LoginKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string LoginValue = "CodexTracker.Tray";
    internal static string LoginCommand => "\"" + Environment.ProcessPath + "\"";

    internal static void MigrateLogin(bool uninstall)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LoginKey, writable: true);
            if (key?.GetValue(LoginValue) is not string existing) return;
            if (uninstall)
            {
                if (string.Equals(existing, LoginCommand, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(LoginValue, false);
            }
            else if (string.Equals(Path.GetFileName(existing.Trim('"')), "CodexTracker.Tray.exe", StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(LoginValue, LoginCommand);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    public void Dispose()
    {
        timer.Dispose();
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
