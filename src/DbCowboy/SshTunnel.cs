using Renci.SshNet;
using Renci.SshNet.Common;

namespace DbCowboy;

/// <summary>The SSH server presented a different host key from the one recorded for this connection.</summary>
public sealed class SshHostKeyChangedException(string host, string expected, string seen)
    : Exception($"The SSH server {host} presented a different host key than the one recorded for this connection.\nRecorded: {expected}\nPresented: {seen}")
{
    public string Seen { get; } = seen;
}

/// <summary>The SSH private key file could not be used: wrong or missing passphrase, or not a key SSH.NET reads.</summary>
public sealed class SshKeyException(string path, bool needsPassphrase, Exception inner) : Exception(inner.Message, inner)
{
    public string Path { get; } = path;
    /// <summary>The key is encrypted and no passphrase was given.</summary>
    public bool NeedsPassphrase { get; } = needsPassphrase;
}

/// <summary>
/// A local port forwarded through an SSH server to the database, for servers that are only reachable from
/// inside their own network. The port listens on 127.0.0.1 only and lives as long as the session.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    readonly SshClient client; readonly ForwardedPortLocal forward;
    public int LocalPort => (int)forward.BoundPort;
    /// <summary>The server's host key fingerprint as seen on this connection, as "SHA256:…".</summary>
    public string HostKey { get; }
    SshTunnel(SshClient client, ForwardedPortLocal forward, string hostKey) { this.client = client; this.forward = forward; HostKey = hostKey; }

    public static async Task<SshTunnel> Open(Profile profile, CancellationToken ct)
    {
        string secret = Store.Unprotect(profile.SshSecret);
        var methods = new List<AuthenticationMethod>();
        if (profile.SshKeyFile.Trim().Length > 0)
        {
            PrivateKeyFile key;
            try { key = secret.Length > 0 ? new PrivateKeyFile(profile.SshKeyFile, secret) : new PrivateKeyFile(profile.SshKeyFile); }
            catch (Exception e) when (e is not (FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)) { throw new SshKeyException(profile.SshKeyFile, e is SshPassPhraseNullOrEmptyException, e); }
            methods.Add(new PrivateKeyAuthenticationMethod(profile.SshUser, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(profile.SshUser, secret));
            // Many servers ask for the password through keyboard-interactive instead of the password method.
            var interactive = new KeyboardInteractiveAuthenticationMethod(profile.SshUser);
            interactive.AuthenticationPrompt += (_, e) => { foreach (var prompt in e.Prompts) prompt.Response = secret; };
            methods.Add(interactive);
        }
        var client = new SshClient(new ConnectionInfo(profile.SshHost, profile.SshPort, profile.SshUser, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(10) }) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        string expected = profile.SshHostKey, seen = "";
        // Trust on first use: the first key is recorded with the connection, and a later change is refused.
        client.HostKeyReceived += (_, e) => { seen = "SHA256:" + e.FingerPrintSHA256; e.CanTrust = expected.Length == 0 || expected == seen; };
        try
        {
            await client.ConnectAsync(ct);
            var forward = new ForwardedPortLocal("127.0.0.1", profile.Host, (uint)profile.Port);
            client.AddForwardedPort(forward); forward.Start();
            return new(client, forward, seen);
        }
        catch (Exception) when (expected.Length > 0 && seen.Length > 0 && seen != expected) { client.Dispose(); throw new SshHostKeyChangedException(profile.SshHost, expected, seen); }
        catch { client.Dispose(); throw; }
    }

    public void Dispose()
    {
        try { if (forward.IsStarted) forward.Stop(); } catch { }
        client.Dispose();
    }
}
