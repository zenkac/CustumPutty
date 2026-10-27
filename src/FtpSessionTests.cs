using System.IO;
using System.Text;

namespace EquipLink;

public static class FtpSessionTests
{
    public static async Task<string> RunAsync()
    {
        var results = new List<string>();
        void Check(string label, bool ok)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + label);
            results.Add("PASS: " + label);
        }
        async Task ExpectCanceled(Task operation)
        {
            try { await operation; Check("Obsolete FTP navigation is canceled", false); }
            catch (OperationCanceledException) { Check("Obsolete FTP navigation is canceled", true); }
        }
        string? oldRoot = AppPaths.OverrideRoot;
        AppPaths.OverrideRoot = Path.Combine(AppPaths.Root, "ftp-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AppPaths.Root);
        try
        {
            await using var first = new FtpTestServer { RequirePassword = true };
            await using var second = new FtpTestServer { RequirePassword = true, Password = "second-session-fixture-password" };
            first.SetFile("/first/first-only.txt", Encoding.UTF8.GetBytes("First server"));
            first.EnsureDirectory("/first/slow-child");
            second.SetFile("/second/second-only.txt", Encoding.UTF8.GetBytes("Second server"));
            var firstSite = first.Site("/first");
            var secondSite = second.Site("/second");
            secondSite.Id = firstSite.Id; // Same saved site edited/reconnected: IDs alone cannot identify a connection.
            firstSite.EncryptedPassword = CredentialProtector.Protect(firstSite.CredentialTarget(), first.Password);
            secondSite.EncryptedPassword = CredentialProtector.Protect(secondSite.CredentialTarget(), second.Password);
            var store = new AppStore();
            using var session = new FtpSession(store);
            await session.ConnectAsync(firstSite, first.Password);
            Check("Session connects at actual configured default path", session.IsConnected && session.CurrentPath == "/first");
            Check("Session exposes latest server entries", session.Entries.Single(x => !x.IsDirectory).Name == "first-only.txt");
            Check("Session retains usable protected transfer credential", session.PasswordForTransfer() == first.Password);
            var saved = AppStore.Load().LastFtpConnection;
            Check("Recent FTP preserves IP ID and default path", saved?.Host == firstSite.Host && saved.Port == firstSite.Port && saved.Username == firstSite.Username && saved.InitialPath == "/first");
            Check("Recent FTP password is encrypted at rest", saved?.HasSavedPassword == true && !File.ReadAllText(AppPaths.Config).Contains(first.Password, StringComparison.Ordinal));

            first.ListDelayMs = 400;
            int listed = first.CommandCount("MLSD");
            var oldNavigation = session.NavigateAsync("/first/slow-child");
            await first.WaitForCommandAsync("MLSD", listed + 1);
            await session.ConnectAsync(secondSite, second.Password);
            await ExpectCanceled(oldNavigation);
            Check("Reconnect discards stale navigation path", session.IsConnected && session.CurrentPath == "/second");
            Check("Reconnect discards stale navigation entries", session.Entries.Count == 1 && session.Entries[0].Name == "second-only.txt");
            Check("Reconnect resets busy state and credential", !session.IsBusy && session.PasswordForTransfer() == second.Password);

            first.ListDelayMs = 0;
            await session.ConnectAsync(firstSite, first.Password);
            first.ListDelayMs = 400;
            listed = first.CommandCount("MLSD");
            var oldFolders = session.ListFoldersAsync("/first");
            await first.WaitForCommandAsync("MLSD", listed + 1);
            await session.ConnectAsync(secondSite, second.Password);
            await ExpectCanceled(oldFolders);
            Check("Folder expansion cannot attach previous connection results", session.Site?.Port == second.Port && session.Entries.All(x => x.Name != "slow-child"));

            first.ListDelayMs = 0;
            first.LoginDelayMs = 400;
            int login = first.CommandCount("PASS");
            var oldConnection = session.ConnectAsync(firstSite, first.Password);
            await first.WaitForCommandAsync("PASS", login + 1);
            await session.ConnectAsync(secondSite, second.Password);
            await oldConnection;
            Check("Latest connection wins over delayed prior login", session.IsConnected && session.Site?.Port == second.Port && session.CurrentPath == "/second");
            Check("Delayed login does not replace most recent saved connection", store.LastFtpConnection?.Port == second.Port && AppStore.Load().LastFtpConnection?.Port == second.Port);

            try { await session.NavigateAsync("/missing"); Check("Missing folder reports server rejection", false); }
            catch (FtpException error) { Check("Missing folder reports server rejection", error.ReplyCode == 550); }
            Check("Rejected navigation preserves actual working path", session.IsConnected && session.CurrentPath == "/second");
            await session.RefreshAsync();
            Check("Session remains usable after synchronized server rejection", !session.IsBusy && session.Error.Length == 0 && session.Entries.Count == 1);

            session.Disconnect();
            Check("Disconnect clears current server path entries and busy state", !session.IsConnected && session.Site == null && session.CurrentPath == "/" && session.Entries.Count == 0 && !session.IsBusy);
            bool credentialRejected = false;
            try { _ = session.PasswordForTransfer(); }
            catch (InvalidOperationException) { credentialRejected = true; }
            Check("Disconnected session cannot supply transfer credentials", credentialRejected);

            using var cancellation = new CancellationTokenSource();
            login = first.CommandCount("PASS");
            var canceledConnection = session.ConnectAsync(firstSite, first.Password, cancellation.Token);
            await first.WaitForCommandAsync("PASS", login + 1);
            cancellation.Cancel();
            try { await canceledConnection; Check("Caller can cancel a pending FTP connection", false); }
            catch (OperationCanceledException) { Check("Caller can cancel a pending FTP connection", true); }
            Check("Canceled connection leaves no usable session", !session.IsConnected && !session.IsBusy);
            await session.ConnectAsync(secondSite, second.Password);
            Check("Session reconnects after canceled connection", session.IsConnected && session.Error.Length == 0);
            var json = File.ReadAllText(AppPaths.Config);
            Check("Connection races never persist plaintext credentials", !json.Contains(first.Password, StringComparison.Ordinal) && !json.Contains(second.Password, StringComparison.Ordinal));
            Check("Export excludes recent connection credential after reconnect", !store.ExportJson().Contains(secondSite.EncryptedPassword!, StringComparison.Ordinal));
            session.Dispose();
            try { await session.ConnectAsync(secondSite, second.Password); Check("Disposed session rejects further connections", false); }
            catch (ObjectDisposedException) { Check("Disposed session rejects further connections", true); }
        }
        finally { AppPaths.OverrideRoot = oldRoot; }
        return string.Join(Environment.NewLine, results);
    }
}
