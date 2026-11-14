using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using Npgsql;

namespace DbCowboy;

public enum CheckState { Pending, Running, Ok, Failed, Skipped }

/// <summary>One stage of a connection attempt. <see cref="Key"/> is ssh, reach, tls, signin or database.</summary>
public sealed record CheckStep(string Key, string Title, CheckState State = CheckState.Pending, string Detail = "");

/// <summary>
/// How far a connection attempt got. <see cref="Fix"/> names the field that most likely needs changing
/// (ca, tls, password, database, port, ssh) or is empty; <see cref="Raw"/> is the driver's own message.
/// </summary>
public sealed record CheckResult(IReadOnlyList<CheckStep> Steps, bool Ok, string Headline, string Advice, string Fix, string Raw, string HostKey);

/// <summary>
/// Connects in stages so a failure names what to change: open the SSH tunnel, reach the server, encrypt,
/// sign in, open the database. It only connects and reads the server version; nothing is written.
/// </summary>
public static class ConnectionCheck
{
    public static List<CheckStep> Plan(Profile p)
    {
        var steps = new List<CheckStep>();
        if (p.Ssh) steps.Add(new("ssh", p.SshHost.Length > 0 ? "Open SSH tunnel to " + p.SshHost : "Open SSH tunnel"));
        steps.Add(new("reach", "Reach the server"));
        steps.Add(new("tls", p.Ssl == "Disable" ? "Encryption off" : "Encrypt · " + TlsName(p.Ssl)));
        steps.Add(new("signin", "Sign in as " + p.Username));
        steps.Add(new("database", "Open database " + p.Database));
        return steps;
    }

    public static string TlsName(string mode) => mode switch { "VerifyFull" => "Verify full", "VerifyCA" => "Verify CA", _ => mode };

    public static async Task<CheckResult> Run(Profile p, string password, Action<IReadOnlyList<CheckStep>>? report, CancellationToken ct)
    {
        var steps = Plan(p); string hostKey = "";
        void Set(string key, CheckState state, string detail = "")
        {
            int i = steps.FindIndex(s => s.Key == key);
            if (i >= 0) steps[i] = steps[i] with { State = state, Detail = detail };
            report?.Invoke(steps.ToList());
        }
        CheckResult Fail(string key, string detail, string advice, string fix, string raw)
        {
            // Everything before the failing stage worked; everything after it was never tried.
            foreach (var step in steps.TakeWhile(s => s.Key != key).Where(s => s.State is CheckState.Pending or CheckState.Running).ToList()) Set(step.Key, step.Key == "tls" && p.Ssl == "Disable" ? CheckState.Skipped : CheckState.Ok);
            foreach (var step in steps.SkipWhile(s => s.Key != key).Skip(1).ToList()) Set(step.Key, CheckState.Pending);
            Set(key, CheckState.Failed, detail);
            string headline = key switch { "ssh" => "Couldn't open the SSH tunnel", "reach" => "Couldn't reach the server", "tls" => "Couldn't set up encryption", "signin" => "Couldn't sign in", _ => "Couldn't open the database" };
            return new(steps, false, headline, advice, fix, raw, hostKey);
        }

        foreach (var (label, path) in new[] { ("CA cert", p.RootCertificate), ("Cert", p.ClientCertificate), ("Key", p.ClientKey) })
            if (path.Trim().Length > 0 && !File.Exists(path)) return Fail("tls", $"The {label} file is no longer at {path}.", "Choose the file again.", "ca", "File not found: " + path);

        SshTunnel? tunnel = null;
        try
        {
            if (p.Ssh)
            {
                Set("ssh", CheckState.Running);
                try { tunnel = await SshTunnel.Open(p, ct); hostKey = tunnel.HostKey; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { var (detail, advice) = SshProblem(p, e); return Fail("ssh", detail, advice, "ssh", e.ToString()); }
                Set("ssh", CheckState.Ok, p.SshHostKey.Length == 0 ? "host key recorded for next time" : "host key matches");
            }
            else
            {
                Set("reach", CheckState.Running);
                var watch = Stopwatch.StartNew();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { using var probe = new TcpClient(); await probe.ConnectAsync(p.Host, p.Port, timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Fail("reach", $"No answer from {p.Host}:{p.Port} after 10 seconds.", "Usual causes: this computer isn't on the server's allow list, a VPN is off, or the port is different (poolers often use 6543).", "port", "Timed out");
                }
                catch (SocketException e)
                {
                    return e.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => Fail("reach", $"The name {p.Host} doesn't resolve to an address.", "Check the host for a typo. A private name may need your VPN.", "host", e.Message),
                        SocketError.ConnectionRefused => Fail("reach", $"{p.Host} refused a connection on port {p.Port}.", "Nothing is listening there. Check the port and that the server is running.", "port", e.Message),
                        _ => Fail("reach", e.Message, "Check the host, the port and your network.", "host", e.Message)
                    };
                }
                Set("reach", CheckState.Ok, $"{watch.ElapsedMilliseconds:N0} ms");
            }

            Set(p.Ssl == "Disable" ? "signin" : "tls", CheckState.Running);
            var db = new Db(p, password, tunnel); tunnel = null;   // the session owns the tunnel from here
            await using (db)
            {
                string version;
                try { version = await db.Test(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { var (key, detail, advice, fix) = Classify(p, e); return Fail(key, detail, advice, fix, e.ToString()); }

                // Facts for the last lines are a courtesy; a role that cannot read them is still connected.
                string tls = "", tables = "";
                try
                {
                    var facts = await db.Query("SELECT (SELECT ssl::text || '|' || coalesce(version,'') FROM pg_stat_ssl WHERE pid=pg_backend_pid()), (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind IN ('r','p') AND n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname !~ '^pg_toast' AND has_schema_privilege(n.oid,'USAGE'))", ct, true);
                    tls = facts.Table.Rows[0][0] as string ?? ""; tables = facts.Table.Rows[0][1] as string ?? "";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }

                if (p.Ssh) Set("reach", CheckState.Ok, "through the tunnel");
                string protocol = tls.Contains('|') ? tls[(tls.IndexOf('|') + 1)..] : "";
                bool encrypted = tls.StartsWith("true", StringComparison.Ordinal) || tls.StartsWith("t|", StringComparison.Ordinal);
                if (p.Ssl == "Disable") Set("tls", CheckState.Skipped, "traffic is not encrypted");
                else if (tls.Length > 0 && !encrypted) Set("tls", CheckState.Skipped, "the server doesn't offer encryption; connected without it");
                else Set("tls", CheckState.Ok, string.Join(" · ", new[] { protocol, p.Ssl switch { "VerifyFull" => "certificate and host name checked", "VerifyCA" => "certificate checked", _ => "identity not checked" } }.Where(part => part.Length > 0)));
                Set("signin", CheckState.Ok);
                Set("database", CheckState.Ok, "PostgreSQL " + version + (int.TryParse(tables, out int count) ? $" · {count:N0} {(count == 1 ? "table" : "tables")}" : ""));
                return new(steps, true, "Connected", "", "", "", hostKey);
            }
        }
        finally { tunnel?.Dispose(); }
    }

    static (string Detail, string Advice) SshProblem(Profile p, Exception e) => e switch
    {
        SshHostKeyChangedException => ($"{p.SshHost} presented a different host key than the one recorded.", "If the server was rebuilt this is expected: choose Forget host key and test again. Otherwise stop; something may be intercepting the connection."),
        Renci.SshNet.Common.SshAuthenticationException => ($"{p.SshHost} rejected the sign-in for {p.SshUser}.", p.SshKeyFile.Length > 0 ? "Check the SSH user, the private key and its passphrase." : "Check the SSH user and password."),
        SshKeyException { NeedsPassphrase: true } => ("The private key is protected by a passphrase.", "Enter the passphrase in the box beside the key."),
        SshKeyException when p.SshSecret.Length > 0 => ("The passphrase doesn't unlock the private key.", "Check the passphrase. If it is right, the file may not be a private key in OpenSSH or PEM format."),
        SshKeyException => ("The private key file could not be read as a key.", "Choose a private key in OpenSSH or PEM format, not the .pub file."),
        UnauthorizedAccessException => ($"Windows won't let DB Cowboy read {p.SshKeyFile}.", "Check the file's permissions, or copy the key somewhere you can read."),
        FileNotFoundException or DirectoryNotFoundException => ($"The private key file is no longer at {p.SshKeyFile}.", "Choose the key file again."),
        SocketException or TimeoutException or Renci.SshNet.Common.SshOperationTimeoutException => ($"No answer from {p.SshHost}:{p.SshPort}.", "Check the SSH host and port, and that this computer is allowed to reach it."),
        System.Security.Cryptography.CryptographicException => ("The saved SSH password could not be read on this Windows account.", "Enter it again."),
        _ => (e.Message, "Check the SSH host, user and key.")
    };

    static (string Key, string Detail, string Advice, string Fix) Classify(Profile p, Exception e)
    {
        if (e is PostgresException pg) return pg.SqlState switch
        {
            "28P01" => ("signin", $"The server rejected the password for {p.Username}.", "Check the password and try again.", "password"),
            "28000" when pg.MessageText.Contains("client certificate", StringComparison.OrdinalIgnoreCase) => ("signin", $"The server wants a client certificate for {p.Username}.", p.ClientCertificate.Length > 0 ? "The applied Cert wasn't accepted. It must be issued for this user by an authority the server trusts." : "Apply this user's Cert and Key.", "cert"),
            "28000" => ("signin", pg.MessageText, "The server's access rules (pg_hba.conf) don't allow this user, address or encryption mode.", pg.MessageText.Contains("no encryption", StringComparison.OrdinalIgnoreCase) ? "tls" : ""),
            "3D000" => ("database", $"There is no database named {p.Database} on this server.", "Check the spelling; database names are case-sensitive.", "database"),
            "53300" => ("signin", "The server has no free connection slots.", "Try again shortly, or connect through the provider's pooler.", ""),
            _ => ("signin", pg.MessageText, "", "")
        };
        bool verifying = p.Ssl is "VerifyFull" or "VerifyCA";
        for (Exception? inner = e; inner != null; inner = inner.InnerException)
        {
            // Thrown while loading the client certificate, before anything is sent.
            if (inner is System.Security.Cryptography.CryptographicException)
                return ("tls", "The client Cert and Key could not be loaded together.", "Check that both are PEM files and that the Key belongs to the Cert.", "cert");
            if (inner is AuthenticationException)
            {
                if (!verifying) return ("tls", inner.Message, "The server and this computer could not agree on encryption.", "tls");
                // The driver only says a callback rejected the certificate, so say what the mode was checking.
                string named = p.Ssl == "VerifyFull" ? $", or isn't issued for {p.Host}" : "";
                return p.RootCertificate.Trim().Length > 0
                    ? ("tls", $"The server's certificate wasn't issued by the applied CA cert{named}.", p.Ssl == "VerifyFull" ? "Check that the CA cert is your provider's. Verify CA skips the host name check." : "Check that the CA cert is your provider's.", "ca")
                    : ("tls", $"The server's certificate comes from an authority Windows doesn't trust{named}.", "Apply your provider's CA cert, or choose Require to encrypt without the identity check.", "ca");
            }
            if (inner is SocketException or TimeoutException or IOException or EndOfStreamException)
                return p.Ssh
                    ? ("reach", $"The SSH server could not reach {p.Host}:{p.Port}.", "Host and port are looked up from the SSH server, so use the name the database has inside that network.", "host")
                    : ("reach", inner.Message, "The server accepted the connection and then dropped it. Check the port: it may not be Postgres, or a pooler may need a different one.", "port");
        }
        if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
            return ("tls", e.Message, p.Ssl == "Disable" ? "The server requires encryption. Choose Require or a verify mode." : "The server may not accept encrypted connections. A local server usually needs Prefer or Disable.", "tls");
        return ("signin", e.Message, "", "");
    }
}
